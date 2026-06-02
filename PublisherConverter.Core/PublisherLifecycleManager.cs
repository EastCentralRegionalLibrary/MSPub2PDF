using System;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    public class PublisherLifecycleManager : IPublisherRenderer
    {
        public const int DefaultMaxConsecutiveFailures = 3;
        private const int ShutdownTimeoutSeconds = 2;

        private int _consecutiveFailureCount;
        private readonly int _maxConsecutiveFailures;
        private readonly object _stateLock = new object();

        // When non-null, Shutdown will dispose the current client and the next
        // operation will lazily create a fresh one via this factory. This is what
        // lets the GUI keep its PublisherLifecycleManager reference and click
        // "Continue" after a circuit-breaker trip without re-wiring the engine.
        private readonly Func<IPublisherWorkerClient>? _workerClientFactory;

        private IPublisherWorkerClient? _workerClient;

        /// <summary>
        /// Dependency-injected client. Once disposed (via Shutdown or Dispose)
        /// this manager cannot be reused — there's no factory to rebuild from.
        /// </summary>
        public PublisherLifecycleManager(IPublisherWorkerClient workerClient, int maxConsecutiveFailures = DefaultMaxConsecutiveFailures)
        {
            _workerClient = workerClient ?? throw new ArgumentNullException(nameof(workerClient));
            _maxConsecutiveFailures = maxConsecutiveFailures;
        }

        /// <summary>
        /// Creates a PublisherLifecycleManager that launches the current executable
        /// in worker mode (the GUI's --mode=worker code path).
        /// </summary>
        public PublisherLifecycleManager(int maxConsecutiveFailures = DefaultMaxConsecutiveFailures)
            : this(workerExecutablePath: null, maxConsecutiveFailures)
        {
        }

        /// <summary>
        /// Creates a PublisherLifecycleManager that launches a specified worker
        /// executable. Pass null to fall back to the current process executable.
        /// The worker client is created lazily and re-created on demand after
        /// Shutdown so the manager can be reused for a follow-up batch.
        /// </summary>
        public PublisherLifecycleManager(string? workerExecutablePath, int maxConsecutiveFailures = DefaultMaxConsecutiveFailures)
        {
            _maxConsecutiveFailures = maxConsecutiveFailures;
            _workerClientFactory = () => new PublisherWorkerClient(
                new ProcessLauncher(workerExecutablePath),
                pipeName => new NamedPipeWorkerTransport(pipeName),
                new DefaultWorkerHealthMonitor(),
                new DefaultTimeoutProvider(60));
        }

        public static PublisherLifecycleManager CreateWithWorkerPath(string workerExecutablePath, int maxConsecutiveFailures = DefaultMaxConsecutiveFailures)
        {
            if (string.IsNullOrEmpty(workerExecutablePath)) throw new ArgumentException("Worker executable path must be provided.", nameof(workerExecutablePath));
            return new PublisherLifecycleManager(workerExecutablePath, maxConsecutiveFailures);
        }

        public void Initialize()
        {
            // Blocks the caller until the worker is connected and a health
            // probe succeeds. Prefer InitializeAsync from GUI threads.
            InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            IPublisherWorkerClient client;
            try
            {
                client = AcquireClient();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_stateLock) { _consecutiveFailureCount++; }
                throw new InvalidOperationException($"Worker initialization failed: {ex.Message}", ex);
            }

            try
            {
                await client.EnsureWorkerStartedAsync(cancellationToken).ConfigureAwait(false);

                var response = await client.SendRequestAsync(
                    new WorkerRequest { Command = "health" },
                    timeoutSeconds: null,
                    cancellationToken).ConfigureAwait(false);

                if (!response.Success)
                {
                    throw new InvalidOperationException($"Worker health check failed: {response.ErrorMessage}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_stateLock) { _consecutiveFailureCount++; }
                throw new InvalidOperationException($"Worker initialization failed: {ex.Message}", ex);
            }
        }

        public void Shutdown()
        {
            IPublisherWorkerClient? toDispose;
            lock (_stateLock)
            {
                toDispose = _workerClient;

                // Reset state. For the self-managed (factory) case, the next
                // InitializeAsync / ExecuteRenderingJobAsync will lazily create
                // a fresh client. For the DI case, _workerClient stays null and
                // subsequent calls will throw — there's no factory to rebuild.
                _workerClient = null;
                _consecutiveFailureCount = 0;
            }

            if (toDispose == null) return;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ShutdownTimeoutSeconds));
                toDispose.SendRequestAsync(
                    new WorkerRequest { Command = "shutdown" },
                    timeoutSeconds: ShutdownTimeoutSeconds,
                    cts.Token).GetAwaiter().GetResult();
            }
            catch
            {
                // Best-effort graceful shutdown.
            }
            finally
            {
                try { toDispose.Dispose(); } catch { }
            }
        }

        public void Recycle()
        {
            IPublisherWorkerClient? client;
            lock (_stateLock) { client = _workerClient; }
            client?.RecycleWorker();
        }

        public void RecordBatchSuccess()
        {
            lock (_stateLock) { _consecutiveFailureCount = 0; }
        }

        public bool RecordBatchFailure()
        {
            lock (_stateLock)
            {
                _consecutiveFailureCount++;
                return _consecutiveFailureCount >= _maxConsecutiveFailures;
            }
        }

        public int ConsecutiveFailures
        {
            get
            {
                lock (_stateLock) return _consecutiveFailureCount;
            }
        }

        public async Task ExecuteRenderingJobAsync(FileRecord record, string sourcePubPath, string targetPdfPath, bool runLinkCheck, int timeoutSeconds, CancellationToken cancellationToken)
        {
            var client = AcquireClient();

            var request = new WorkerRequest
            {
                Command = "render",
                RenderJob = new RenderJob
                {
                    SourcePubPath = sourcePubPath,
                    TargetPdfPath = targetPdfPath,
                    RunLinkCheck = runLinkCheck
                }
            };

            var response = await client.SendRequestAsync(request, timeoutSeconds, cancellationToken).ConfigureAwait(false);

            if (!response.Success)
            {
                throw new InvalidOperationException(response.ErrorMessage ?? "Unknown worker error during rendering.");
            }

            if (response.RenderResult != null)
            {
                record.MissingAssetsCount = response.RenderResult.MissingAssetsCount;
                record.MissingAssetsList = response.RenderResult.MissingAssetsList;
            }
        }

        private IPublisherWorkerClient AcquireClient()
        {
            lock (_stateLock)
            {
                if (_workerClient != null) return _workerClient;

                if (_workerClientFactory == null)
                {
                    throw new InvalidOperationException(
                        "Worker client has been disposed and this manager was constructed " +
                        "with an injected client (no factory available). Create a new " +
                        "PublisherLifecycleManager to continue.");
                }

                _workerClient = _workerClientFactory();
                return _workerClient;
            }
        }
    }
}

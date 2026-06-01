using System;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    public class PublisherLifecycleManager : IPublisherRenderer
    {
        public const int DefaultMaxConsecutiveFailures = 3;
        private const int ShutdownTimeoutSeconds = 2;

        private int _consecutiveFailureCount = 0;
        private readonly int _maxConsecutiveFailures;
        private readonly object _stateLock = new object();

        private readonly IPublisherWorkerClient _workerClient;

        public PublisherLifecycleManager(IPublisherWorkerClient workerClient, int maxConsecutiveFailures = DefaultMaxConsecutiveFailures)
        {
            _workerClient = workerClient ?? throw new ArgumentNullException(nameof(workerClient));
            _maxConsecutiveFailures = maxConsecutiveFailures;
        }

        /// <summary>
        /// Creates a PublisherLifecycleManager that launches the current executable
        /// in worker mode. Use this from the GUI exe (which knows how to handle
        /// --mode=worker via Program.cs).
        /// </summary>
        public PublisherLifecycleManager(int maxConsecutiveFailures = DefaultMaxConsecutiveFailures)
            : this(workerExecutablePath: null, maxConsecutiveFailures)
        {
        }

        /// <summary>
        /// Creates a PublisherLifecycleManager that launches a specified worker
        /// executable. Pass null to fall back to the current process executable.
        /// </summary>
        public PublisherLifecycleManager(string? workerExecutablePath, int maxConsecutiveFailures = DefaultMaxConsecutiveFailures)
        {
            _maxConsecutiveFailures = maxConsecutiveFailures;

            _workerClient = new PublisherWorkerClient(
                new ProcessLauncher(workerExecutablePath),
                pipeName => new NamedPipeWorkerTransport(pipeName),
                new DefaultWorkerHealthMonitor(),
                new DefaultTimeoutProvider(60));
        }

        /// <summary>
        /// Factory method for callers that prefer a named factory over the
        /// nullable-string constructor.
        /// </summary>
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
            try
            {
                await _workerClient.EnsureWorkerStartedAsync(cancellationToken).ConfigureAwait(false);

                var response = await _workerClient.SendRequestAsync(
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
                lock (_stateLock)
                {
                    _consecutiveFailureCount++;
                }
                throw new InvalidOperationException($"Worker initialization failed: {ex.Message}", ex);
            }
        }

        public void Shutdown()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ShutdownTimeoutSeconds));
                _workerClient.SendRequestAsync(
                    new WorkerRequest { Command = "shutdown" },
                    timeoutSeconds: ShutdownTimeoutSeconds,
                    cts.Token).GetAwaiter().GetResult();
            }
            catch
            {
                // Best-effort graceful shutdown. The Dispose below will
                // force-terminate the worker if it didn't exit voluntarily.
            }
            finally
            {
                _workerClient.Dispose();
            }
        }

        public void Recycle()
        {
            _workerClient.RecycleWorker();
        }

        public void RecordBatchSuccess()
        {
            lock (_stateLock)
            {
                _consecutiveFailureCount = 0;
            }
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

            var response = await _workerClient.SendRequestAsync(request, timeoutSeconds, cancellationToken).ConfigureAwait(false);

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
    }
}

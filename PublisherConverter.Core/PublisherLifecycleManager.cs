using System;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    public class PublisherLifecycleManager : IPublisherRenderer
    {
        public const int DefaultMaxConsecutiveFailures = 3;

        private int _consecutiveFailureCount = 0;
        private readonly int _maxConsecutiveFailures;
        private readonly object _stateLock = new object();

        private readonly IPublisherWorkerClient _workerClient;

        public PublisherLifecycleManager(IPublisherWorkerClient workerClient, int maxConsecutiveFailures = DefaultMaxConsecutiveFailures)
        {
            _workerClient = workerClient;
            _maxConsecutiveFailures = maxConsecutiveFailures;
        }

        /// <summary>
        /// Creates a PublisherLifecycleManager with default worker infrastructure.
        /// The worker process will be the current executable (or specified path).
        /// </summary>
        public PublisherLifecycleManager(int maxConsecutiveFailures = DefaultMaxConsecutiveFailures)
        {
            _maxConsecutiveFailures = maxConsecutiveFailures;

            string pipeName = $"PublisherWorker_{Guid.NewGuid():N}";
            _workerClient = new PublisherWorkerClient(
                new ProcessLauncher(pipeName, workerExecutablePath: null),
                () => new NamedPipeWorkerTransport(pipeName),
                new DefaultWorkerHealthMonitor(),
                new DefaultTimeoutProvider(60) // Default 60s
            );
        }

        /// <summary>
        /// Creates a PublisherLifecycleManager with a specific worker executable path.
        /// </summary>
        /// <param name="workerExecutablePath">Path to the GUI executable to run as worker</param>
        /// <param name="maxConsecutiveFailures">Max consecutive failures before circuit breaker</param>
        private PublisherLifecycleManager(string workerExecutablePath, int maxConsecutiveFailures)
        {
            _maxConsecutiveFailures = maxConsecutiveFailures;

            string pipeName = $"PublisherWorker_{Guid.NewGuid():N}";
            _workerClient = new PublisherWorkerClient(
                new ProcessLauncher(pipeName, workerExecutablePath),
                () => new NamedPipeWorkerTransport(pipeName),
                new DefaultWorkerHealthMonitor(),
                new DefaultTimeoutProvider(60)
            );
        }

        /// <summary>
        /// Factory method to create a PublisherLifecycleManager with a specific worker executable.
        /// </summary>
        public static PublisherLifecycleManager CreateWithWorkerPath(string workerExecutablePath, int maxConsecutiveFailures = DefaultMaxConsecutiveFailures)
        {
            return new PublisherLifecycleManager(workerExecutablePath, maxConsecutiveFailures);
        }

        public void Initialize()
        {
            // Synchronous initialization for backwards compatibility
            // Warning: This will block the calling thread. Use InitializeAsync() on GUI threads.
            // Runs on thread pool to avoid blocking if called from GUI context
            InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            // Ensure worker process is started and responsive
            try
            {
                await _workerClient.EnsureWorkerStartedAsync(cancellationToken);
                
                // Send health check to verify worker is operational
                var request = new WorkerRequest { Command = "health" };
                var response = await _workerClient.SendRequestAsync(request, null, cancellationToken);
                
                if (!response.Success)
                {
                    throw new InvalidOperationException($"Worker health check failed: {response.ErrorMessage}");
                }
            }
            catch (Exception ex)
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
            // Graceful shutdown of worker process
            try
            {
                // Send shutdown request with timeout
                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                _workerClient.SendRequestAsync(new WorkerRequest { Command = "shutdown" }, 1, cts.Token).Wait(1000);
            }
            catch
            {
                // Ignore errors during shutdown
            }
            finally
            {
                // Always dispose to ensure worker process is terminated
                _workerClient?.Dispose();
            }
        }

        public void Recycle()
        {
            lock (_stateLock)
            {
                _workerClient.RecycleWorker();
            }
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

            var response = await _workerClient.SendRequestAsync(request, timeoutSeconds, cancellationToken);

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

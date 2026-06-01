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

        // Default constructor for GUI/backwards compatibility if needed, though we should prefer DI
        public PublisherLifecycleManager(int maxConsecutiveFailures = DefaultMaxConsecutiveFailures)
        {
            _maxConsecutiveFailures = maxConsecutiveFailures;

            string pipeName = $"PublisherWorker_{Guid.NewGuid():N}";
            _workerClient = new PublisherWorkerClient(
                new ProcessLauncher(pipeName),
                () => new NamedPipeWorkerTransport(pipeName),
                new DefaultWorkerHealthMonitor(),
                new DefaultTimeoutProvider(60) // Default 60s
            );
        }

        public void Initialize()
        {
            lock (_stateLock)
            {
                _workerClient.EnsureWorkerStarted();

                // Verify engine availability by sending a health check
                try
                {
                    var response = _workerClient.SendRequestAsync(new WorkerRequest { Command = "health" }, null, CancellationToken.None).GetAwaiter().GetResult();
                    if (!response.Success)
                    {
                        throw new InvalidOperationException($"Worker health check failed: {response.ErrorMessage}");
                    }
                }
                catch (Exception ex)
                {
                    _consecutiveFailureCount++;
                    throw new InvalidOperationException($"Inbound worker factory validation failed: {ex.Message}", ex);
                }
            }
        }

        public void Shutdown()
        {
            lock (_stateLock)
            {
                // Best effort shutdown
                try
                {
                    _workerClient.SendRequestAsync(new WorkerRequest { Command = "shutdown" }, 1, new CancellationTokenSource(1000).Token).Wait();
                }
                catch { }
                _workerClient.Dispose();
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

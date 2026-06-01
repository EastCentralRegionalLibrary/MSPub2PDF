using System;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    public class PublisherWorkerClient : IPublisherWorkerClient
    {
        private readonly IProcessLauncher _launcher;
        private readonly Func<IWorkerTransport> _transportFactory;
        private readonly IWorkerHealthMonitor _healthMonitor;
        private readonly ITimeoutProvider _timeoutProvider;

        private IProcessHandle? _processHandle;
        private IWorkerTransport? _transport;
        private readonly object _lifecycleLock = new object();
        private readonly SemaphoreSlim _ipcSemaphore = new SemaphoreSlim(1, 1);

        public PublisherWorkerClient(
            IProcessLauncher launcher,
            Func<IWorkerTransport> transportFactory,
            IWorkerHealthMonitor healthMonitor,
            ITimeoutProvider timeoutProvider)
        {
            _launcher = launcher;
            _transportFactory = transportFactory;
            _healthMonitor = healthMonitor;
            _timeoutProvider = timeoutProvider;
        }

        public bool IsHealthy => _healthMonitor.IsProcessHealthy(_processHandle);

        public void EnsureWorkerStarted()
        {
            lock (_lifecycleLock)
            {
                if (IsHealthy) return;
                StartNewWorker();
            }
        }

        public void RecycleWorker()
        {
            lock (_lifecycleLock)
            {
                TeardownWorker();
                StartNewWorker();
            }
        }

        private void StartNewWorker()
        {
            TeardownWorker();
            _processHandle = _launcher.StartWorker();
            _transport = _transportFactory();
            _transport.Connect();
        }

        private void TeardownWorker()
        {
            _transport?.Dispose();
            _transport = null;

            if (_processHandle != null)
            {
                _processHandle.Kill();
                _processHandle.Dispose();
                _processHandle = null;
            }
        }

        public async Task<WorkerResponse> SendRequestAsync(WorkerRequest request, int? timeoutSeconds, CancellationToken cancellationToken)
        {
            await _ipcSemaphore.WaitAsync(cancellationToken);
            try
            {
                EnsureWorkerStarted();

                TimeSpan timeout = timeoutSeconds.HasValue
                    ? TimeSpan.FromSeconds(timeoutSeconds.Value)
                    : _timeoutProvider.GetTimeout(request.Command);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);

                try
                {
                    IWorkerTransport transport;
                    lock (_lifecycleLock)
                    {
                        if (_transport == null) throw new InvalidOperationException("Worker transport not initialized.");
                        transport = _transport;
                    }

                    await transport.SendRequestAsync(request, cts.Token);
                    return await transport.ReceiveResponseAsync(cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    RecycleWorker();
                    throw new TimeoutException($"Worker request timed out after {timeout.TotalSeconds}s.");
                }
                catch (Exception)
                {
                    RecycleWorker();
                    throw;
                }
            }
            finally
            {
                _ipcSemaphore.Release();
            }
        }

        public void Dispose()
        {
            TeardownWorker();
            _ipcSemaphore.Dispose();
        }
    }
}

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
            // Synchronous version for backwards compatibility (e.g., PublisherWorkerHost.Run())
            EnsureWorkerStartedAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public async Task EnsureWorkerStartedAsync(CancellationToken cancellationToken)
        {
            lock (_lifecycleLock)
            {
                if (IsHealthy) return;
            }
            
            await StartNewWorkerAsync(cancellationToken);
        }

        public void RecycleWorker()
        {
            lock (_lifecycleLock)
            {
                TeardownWorker();
            }
            // Don't await here - let background task handle restart
        }

        private async Task StartNewWorkerAsync(CancellationToken cancellationToken)
        {
            lock (_lifecycleLock)
            {
                TeardownWorker();
                _processHandle = _launcher.StartWorker();
                _transport = _transportFactory();
            }

            // Connect outside the lock to avoid blocking other operations
            try
            {
                await _transport.ConnectAsync(cancellationToken, timeoutMs: 30000);
            }
            catch (Exception ex)
            {
                lock (_lifecycleLock)
                {
                    TeardownWorker();
                }
                throw new InvalidOperationException($"Failed to connect to worker process: {ex.Message}", ex);
            }
        }

        private void StartNewWorker()
        {
            // Synchronous wrapper for backwards compatibility
            StartNewWorkerAsync(CancellationToken.None).GetAwaiter().GetResult();
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
                await EnsureWorkerStartedAsync(cancellationToken);

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
                    lock (_lifecycleLock)
                    {
                        TeardownWorker();
                    }
                    throw new TimeoutException($"Worker request timed out after {timeout.TotalSeconds}s.");
                }
                catch (Exception)
                {
                    lock (_lifecycleLock)
                    {
                        TeardownWorker();
                    }
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
            lock (_lifecycleLock)
            {
                TeardownWorker();
            }
            _ipcSemaphore.Dispose();
        }
    }
}

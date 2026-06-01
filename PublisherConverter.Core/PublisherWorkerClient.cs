using System;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    public class PublisherWorkerClient : IPublisherWorkerClient
    {
        public const int DefaultConnectionTimeoutMs = 30000;

        private readonly IProcessLauncher _launcher;
        private readonly Func<string, IWorkerTransport> _transportFactory;
        private readonly IWorkerHealthMonitor _healthMonitor;
        private readonly ITimeoutProvider _timeoutProvider;
        private readonly int _connectionTimeoutMs;

        private IProcessHandle? _processHandle;
        private IWorkerTransport? _transport;
        private bool _disposed;

        private readonly object _lifecycleLock = new object();
        private readonly SemaphoreSlim _startSemaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _ipcSemaphore = new SemaphoreSlim(1, 1);

        public PublisherWorkerClient(
            IProcessLauncher launcher,
            Func<string, IWorkerTransport> transportFactory,
            IWorkerHealthMonitor healthMonitor,
            ITimeoutProvider timeoutProvider,
            int connectionTimeoutMs = DefaultConnectionTimeoutMs)
        {
            _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
            _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
            _healthMonitor = healthMonitor ?? throw new ArgumentNullException(nameof(healthMonitor));
            _timeoutProvider = timeoutProvider ?? throw new ArgumentNullException(nameof(timeoutProvider));
            if (connectionTimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(connectionTimeoutMs));
            _connectionTimeoutMs = connectionTimeoutMs;
        }

        public bool IsHealthy
        {
            get
            {
                lock (_lifecycleLock)
                {
                    return _healthMonitor.IsProcessHealthy(_processHandle);
                }
            }
        }

        public async Task EnsureWorkerStartedAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            // Serialize concurrent starts so at most one worker process is
            // launched even under contention.
            await _startSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsHealthy) return;
                await StartNewWorkerAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _startSemaphore.Release();
            }
        }

        public void RecycleWorker()
        {
            lock (_lifecycleLock)
            {
                TeardownWorker();
            }
            // Next SendRequestAsync / EnsureWorkerStartedAsync will spin a fresh worker.
        }

        private async Task StartNewWorkerAsync(CancellationToken cancellationToken)
        {
            // Generate a unique pipe name per spawn so that a recycled worker
            // doesn't collide with the previous one (relevant on Unix where
            // Kill() can leave the previous socket file behind).
            string pipeName = $"MSPub2PDF_{Guid.NewGuid():N}";

            IProcessHandle handle;
            IWorkerTransport transport;

            lock (_lifecycleLock)
            {
                TeardownWorker();
                handle = _launcher.StartWorker(pipeName);
                transport = _transportFactory(pipeName);
                _processHandle = handle;
                _transport = transport;
            }

            try
            {
                await transport.ConnectAsync(cancellationToken, timeoutMs: _connectionTimeoutMs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_lifecycleLock)
                {
                    if (ReferenceEquals(_transport, transport))
                    {
                        TeardownWorker();
                    }
                }
                throw new InvalidOperationException($"Failed to connect to worker process: {ex.Message}", ex);
            }
        }

        private void TeardownWorker()
        {
            // Caller must hold _lifecycleLock.
            try { _transport?.Dispose(); } catch { }
            _transport = null;

            if (_processHandle != null)
            {
                try { _processHandle.Kill(); } catch { }
                try { _processHandle.Dispose(); } catch { }
                _processHandle = null;
            }
        }

        public async Task<WorkerResponse> SendRequestAsync(WorkerRequest request, int? timeoutSeconds, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            await _ipcSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await EnsureWorkerStartedAsync(cancellationToken).ConfigureAwait(false);

                TimeSpan timeout = timeoutSeconds.HasValue
                    ? TimeSpan.FromSeconds(timeoutSeconds.Value)
                    : _timeoutProvider.GetTimeout(request.Command);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);

                IWorkerTransport transport;
                lock (_lifecycleLock)
                {
                    if (_transport == null) throw new InvalidOperationException("Worker transport not initialized.");
                    transport = _transport;
                }

                try
                {
                    await transport.SendRequestAsync(request, cts.Token).ConfigureAwait(false);
                    return await transport.ReceiveResponseAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    lock (_lifecycleLock) { TeardownWorker(); }
                    throw new TimeoutException($"Worker request timed out after {timeout.TotalSeconds}s.");
                }
                catch
                {
                    lock (_lifecycleLock) { TeardownWorker(); }
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
            if (_disposed) return;
            _disposed = true;

            lock (_lifecycleLock)
            {
                TeardownWorker();
            }
            _startSemaphore.Dispose();
            _ipcSemaphore.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PublisherWorkerClient));
        }
    }
}

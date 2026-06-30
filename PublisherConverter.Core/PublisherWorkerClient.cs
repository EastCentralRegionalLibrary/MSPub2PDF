using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    public class PublisherWorkerClient : IPublisherWorkerClient
    {
        public const int DefaultConnectionTimeoutMs = 30000;

        // Orderly teardown (Dispose/recycle) closes the transport, then waits up
        // to this budget for the worker to exit on its own before force-killing.
        // The wait is what lets the worker run PublisherComRenderer.Dispose() →
        // mspub Quit() instead of being killed mid-flight (which would orphan the
        // DCOM-activated mspub.exe). Overridable via the ctor for fast tests.
        public static readonly TimeSpan DefaultGracefulExitTimeout = TimeSpan.FromSeconds(3);
        private const int GracefulExitPollMs = 25;

        private readonly IProcessLauncher _launcher;
        private readonly Func<string, IWorkerTransport> _transportFactory;
        private readonly IWorkerHealthMonitor _healthMonitor;
        private readonly ITimeoutProvider _timeoutProvider;
        private readonly int _connectionTimeoutMs;
        private readonly TimeSpan _gracefulExitTimeout;

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
            int connectionTimeoutMs = DefaultConnectionTimeoutMs,
            TimeSpan? gracefulExitTimeout = null)
        {
            _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
            _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
            _healthMonitor = healthMonitor ?? throw new ArgumentNullException(nameof(healthMonitor));
            _timeoutProvider = timeoutProvider ?? throw new ArgumentNullException(nameof(timeoutProvider));
            if (connectionTimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(connectionTimeoutMs));
            _connectionTimeoutMs = connectionTimeoutMs;
            _gracefulExitTimeout = gracefulExitTimeout ?? DefaultGracefulExitTimeout;
            if (_gracefulExitTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(gracefulExitTimeout));
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
                // Recycle is an orderly teardown: let the worker exit cleanly so
                // its mspub.exe is released via Quit() rather than orphaned.
                TeardownWorker(graceful: true);
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
                // Pre-start cleanup of any stale handle: kill promptly (it is
                // normally already null/exited here, and we want the new worker
                // up without waiting on a defunct one).
                TeardownWorker(graceful: false);
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
                        // Connect failed — the worker may be wedged; kill promptly.
                        TeardownWorker(graceful: false);
                    }
                }
                throw new InvalidOperationException($"Failed to connect to worker process: {ex.Message}", ex);
            }
        }

        private void TeardownWorker(bool graceful)
        {
            // Caller must hold _lifecycleLock.

            // Dispose the transport first in every path. Closing the pipe is the
            // signal the worker's host loop waits on: it ends, the worker exits,
            // and PublisherComRenderer.Dispose() → mspub Quit() runs. A hard kill
            // before this skips that release (and can't reach the DCOM-activated
            // mspub anyway), which is what left orphaned mspub.exe behind.
            try { _transport?.Dispose(); } catch { }
            _transport = null;

            if (_processHandle != null)
            {
                var handle = _processHandle;

                // Orderly teardown waits for the worker to exit on its own (now
                // that the pipe is closed) before forcing it. Timeout/error
                // recovery passes graceful:false so a hung/faulted worker is
                // killed promptly.
                if (!graceful || !WaitForExit(handle, _gracefulExitTimeout))
                {
                    try { handle.Kill(); } catch { }
                }
                try { handle.Dispose(); } catch { }
                _processHandle = null;
            }
        }

        // Polls HasExited up to the timeout. Returns true if the process exited
        // on its own within the budget, false if it overstayed (caller kills it).
        private static bool WaitForExit(IProcessHandle handle, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                bool exited;
                try { exited = handle.HasExited; }
                catch { exited = true; } // handle invalidated → treat as gone
                if (exited) return true;
                if (sw.Elapsed >= timeout) return false;
                Thread.Sleep(GracefulExitPollMs);
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
                    // Timeout recovery: a hung worker must be killed promptly, not
                    // waited on — skip the graceful exit budget.
                    lock (_lifecycleLock) { TeardownWorker(graceful: false); }
                    throw new TimeoutException($"Worker request timed out after {timeout.TotalSeconds}s.");
                }
                catch
                {
                    // Transport/protocol fault: the worker is suspect — kill promptly.
                    lock (_lifecycleLock) { TeardownWorker(graceful: false); }
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
                // Orderly shutdown: give the worker the graceful exit window so
                // it releases mspub.exe via Quit() instead of being orphaned.
                TeardownWorker(graceful: true);
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

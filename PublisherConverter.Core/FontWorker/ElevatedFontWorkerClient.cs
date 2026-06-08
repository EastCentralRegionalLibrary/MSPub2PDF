using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FontWorker
{
    /// <summary>
    /// Default <see cref="IElevatedFontWorkerClient"/>. Mirrors the proven
    /// lifecycle of the Publisher render worker client: a single live process,
    /// serialized starts, serialized IPC, per-request timeouts, and teardown on
    /// any failure. All collaborators are injected, so the whole client is
    /// exercised on Linux with fakes — no elevation, pipes, or Windows APIs.
    ///
    /// Transport orientation: this client lives in the non-elevated main
    /// process and is wired with a <em>server</em> transport, while the elevated
    /// worker connects as the client. That inversion is required by Windows
    /// Mandatory Integrity Control — see FontWorkerHost.CreateNamedPipeHost.
    /// The client itself is transport-agnostic; the orientation is a wiring
    /// decision made by the injected transport factory.
    /// </summary>
    public sealed class ElevatedFontWorkerClient : IElevatedFontWorkerClient
    {
        public static readonly TimeSpan DefaultConnectionTimeout = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(5);

        private readonly IFontWorkerProcessLauncher _launcher;
        private readonly Func<string, IFontWorkerTransport> _transportFactory;
        private readonly IWorkerHealthMonitor _healthMonitor;
        private readonly IStructuredLogger _logger;
        private readonly TimeSpan _connectionTimeout;
        private readonly TimeSpan _startupTimeout;
        private readonly TimeSpan _defaultRequestTimeout;

        private readonly object _lifecycleLock = new object();
        private readonly SemaphoreSlim _startSemaphore = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _ipcSemaphore = new SemaphoreSlim(1, 1);

        private IProcessHandle? _processHandle;
        private IFontWorkerTransport? _transport;
        private bool _disposed;

        public ElevatedFontWorkerClient(
            IFontWorkerProcessLauncher launcher,
            Func<string, IFontWorkerTransport> transportFactory,
            IWorkerHealthMonitor healthMonitor,
            IStructuredLogger? logger = null,
            TimeSpan? connectionTimeout = null,
            TimeSpan? startupTimeout = null,
            TimeSpan? defaultRequestTimeout = null)
        {
            _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
            _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
            _healthMonitor = healthMonitor ?? throw new ArgumentNullException(nameof(healthMonitor));
            _logger = logger ?? NullStructuredLogger.Instance;
            _connectionTimeout = connectionTimeout ?? DefaultConnectionTimeout;
            _startupTimeout = startupTimeout ?? DefaultStartupTimeout;
            _defaultRequestTimeout = defaultRequestTimeout ?? DefaultRequestTimeout;
        }

        public bool IsHealthy
        {
            get
            {
                lock (_lifecycleLock)
                {
                    return _transport != null && _healthMonitor.IsProcessHealthy(_processHandle);
                }
            }
        }

        public async Task EnsureStartedAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

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

        private async Task StartNewWorkerAsync(CancellationToken cancellationToken)
        {
            string pipeName = $"MSPub2PDF_FontWorker_{Guid.NewGuid():N}";
            string correlationId = FontWorkerProtocol.NewCorrelationId();

            IProcessHandle handle;
            IFontWorkerTransport transport;
            lock (_lifecycleLock)
            {
                TeardownWorker();
                try
                {
                    handle = _launcher.StartFontWorker(pipeName);
                }
                catch (Exception ex)
                {
                    _logger.Error("client.launch.failed", correlationId, Fields(("error", ex.Message)));
                    throw new FontWorkerStartupException($"Failed to launch font worker: {ex.Message}", ex);
                }
                transport = _transportFactory(pipeName);
                _processHandle = handle;
                _transport = transport;
            }

            _logger.Info("client.worker.launched", correlationId, Fields(("pid", SafePid(handle)), ("pipe", pipeName)));

            // Startup contract: connect and pass a health check within the
            // startup timeout, or fail startup and terminate the worker.
            try
            {
                await transport.ConnectAsync(cancellationToken, timeoutMs: (int)_connectionTimeout.TotalMilliseconds).ConfigureAwait(false);

                var health = await SendInternalAsync(
                    transport,
                    new FontWorkerRequest { CorrelationId = correlationId, Type = FontWorkerMessageType.HealthCheck, HealthCheck = new HealthCheckRequest() },
                    _startupTimeout,
                    cancellationToken).ConfigureAwait(false);

                if (health.HealthCheck?.Healthy != true)
                {
                    throw new FontWorkerStartupException("Worker did not report healthy on startup health check.");
                }

                if (health.HealthCheck?.Elevated == false)
                {
                    // Launched but not elevated — privileged installs will fail.
                    _logger.Warn("client.worker.not_elevated", correlationId);
                }

                _logger.Info("client.worker.ready", correlationId, Fields(("elevated", health.HealthCheck?.Elevated)));
            }
            catch (Exception ex)
            {
                lock (_lifecycleLock)
                {
                    if (ReferenceEquals(_transport, transport)) TeardownWorker();
                }
                _logger.Error("client.startup.failed", correlationId, Fields(("error", ex.Message)));
                if (ex is FontWorkerStartupException) throw;
                throw new FontWorkerStartupException($"Font worker failed to become ready: {ex.Message}", ex);
            }
        }

        public Task<HealthCheckResponse?> CheckHealthAsync(string correlationId, int? timeoutSeconds, CancellationToken cancellationToken)
        {
            var request = new FontWorkerRequest { CorrelationId = correlationId, Type = FontWorkerMessageType.HealthCheck, HealthCheck = new HealthCheckRequest() };
            return ExecuteAsync(request, timeoutSeconds, cancellationToken, response => response.HealthCheck, _ => (HealthCheckResponse?)null);
        }

        public Task<InstallCapabilityResponse> InstallCapabilityAsync(InstallCapabilityRequest request, string correlationId, int? timeoutSeconds, CancellationToken cancellationToken)
        {
            var envelope = new FontWorkerRequest { CorrelationId = correlationId, Type = FontWorkerMessageType.InstallCapability, InstallCapability = request };
            return ExecuteAsync(
                envelope,
                timeoutSeconds,
                cancellationToken,
                response => response.InstallCapability ?? InstallCapabilityResponse.Failed(request.CapabilityName, response.EnvelopeError ?? "Worker returned no payload."),
                detail => InstallCapabilityResponse.Failed(request.CapabilityName, detail));
        }

        public Task<InstallCapabilitiesBatchResponse> InstallCapabilitiesBatchAsync(InstallCapabilitiesBatchRequest request, string correlationId, int? timeoutSeconds, CancellationToken cancellationToken)
        {
            var envelope = new FontWorkerRequest { CorrelationId = correlationId, Type = FontWorkerMessageType.InstallCapabilitiesBatch, InstallCapabilitiesBatch = request };
            return ExecuteAsync(
                envelope,
                timeoutSeconds,
                cancellationToken,
                response => response.InstallCapabilitiesBatch ?? FailedBatch(request, response.EnvelopeError ?? "Worker returned no payload."),
                detail => FailedBatch(request, detail));
        }

        public Task<InstallFontResponse> InstallFontAsync(InstallFontRequest request, string correlationId, int? timeoutSeconds, CancellationToken cancellationToken)
        {
            var envelope = new FontWorkerRequest { CorrelationId = correlationId, Type = FontWorkerMessageType.InstallFont, InstallFont = request };
            return ExecuteAsync(
                envelope,
                timeoutSeconds,
                cancellationToken,
                response => response.InstallFont ?? InstallFontResponse.Failed(request.FamilyName, response.EnvelopeError ?? "Worker returned no payload."),
                detail => InstallFontResponse.Failed(request.FamilyName, detail));
        }

        public async Task ShutdownAsync(CancellationToken cancellationToken)
        {
            if (_disposed) return;

            IFontWorkerTransport? transport;
            lock (_lifecycleLock) { transport = _transport; }
            if (transport == null) return;

            string correlationId = FontWorkerProtocol.NewCorrelationId();
            await _ipcSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                try
                {
                    await SendInternalAsync(
                        transport,
                        new FontWorkerRequest { CorrelationId = correlationId, Type = FontWorkerMessageType.Shutdown, Shutdown = new ShutdownRequest() },
                        TimeSpan.FromSeconds(5),
                        cancellationToken).ConfigureAwait(false);
                    _logger.Info("client.shutdown.ack", correlationId);
                }
                catch (Exception ex)
                {
                    _logger.Warn("client.shutdown.no_ack", correlationId, Fields(("error", ex.Message)));
                }
                finally
                {
                    lock (_lifecycleLock) { TeardownWorker(); }
                }
            }
            finally
            {
                _ipcSemaphore.Release();
            }
        }

        // -----------------------------
        // Request execution
        // -----------------------------

        private async Task<TResult> ExecuteAsync<TResult>(
            FontWorkerRequest request,
            int? timeoutSeconds,
            CancellationToken cancellationToken,
            Func<FontWorkerResponse, TResult> onSuccess,
            Func<string, TResult> onFailure)
        {
            ThrowIfDisposed();

            await _ipcSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                IFontWorkerTransport transport;
                lock (_lifecycleLock)
                {
                    if (_transport == null || !_healthMonitor.IsProcessHealthy(_processHandle))
                    {
                        // No live worker — fail fast without relaunching. The
                        // coordinator owns the decision to (re)launch.
                        _logger.Warn("client.request.no_worker", request.CorrelationId, Fields(("type", request.Type.ToString())));
                        return onFailure("No healthy worker available.");
                    }
                    transport = _transport;
                }

                TimeSpan timeout = timeoutSeconds.HasValue && timeoutSeconds.Value > 0
                    ? TimeSpan.FromSeconds(timeoutSeconds.Value)
                    : _defaultRequestTimeout;

                _logger.Info("client.request", request.CorrelationId, Fields(("type", request.Type.ToString()), ("timeoutSeconds", timeout.TotalSeconds)));

                try
                {
                    var response = await SendInternalAsync(transport, request, timeout, cancellationToken).ConfigureAwait(false);
                    _logger.Info("client.response", request.CorrelationId, Fields(("type", request.Type.ToString()), ("envelopeSuccess", response.EnvelopeSuccess)));
                    return onSuccess(response);
                }
                catch (FontWorkerTimeoutException ex)
                {
                    lock (_lifecycleLock) { TeardownWorker(); }
                    _logger.Error("client.request.timeout", request.CorrelationId, Fields(("type", request.Type.ToString()), ("error", ex.Message)));
                    return onFailure(ex.Message);
                }
                catch (Exception ex)
                {
                    lock (_lifecycleLock) { TeardownWorker(); }
                    _logger.Error("client.request.failed", request.CorrelationId, Fields(("type", request.Type.ToString()), ("error", ex.Message)));
                    return onFailure($"Worker communication failed: {ex.Message}");
                }
            }
            finally
            {
                _ipcSemaphore.Release();
            }
        }

        private static async Task<FontWorkerResponse> SendInternalAsync(
            IFontWorkerTransport transport,
            FontWorkerRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            try
            {
                await transport.SendRequestAsync(request, cts.Token).ConfigureAwait(false);
                return await transport.ReceiveResponseAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new FontWorkerTimeoutException($"Font worker request timed out after {timeout.TotalSeconds:F0}s.");
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_lifecycleLock) { TeardownWorker(); }
            _startSemaphore.Dispose();
            _ipcSemaphore.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ElevatedFontWorkerClient));
        }

        private static InstallCapabilitiesBatchResponse FailedBatch(InstallCapabilitiesBatchRequest request, string detail)
        {
            var results = new List<InstallCapabilityResponse>();
            foreach (var name in request.CapabilityNames ?? Array.Empty<string>())
            {
                results.Add(InstallCapabilityResponse.Failed(name, detail));
            }
            return new InstallCapabilitiesBatchResponse { Results = results };
        }

        private static object SafePid(IProcessHandle handle)
        {
            try { return handle.Id; } catch { return -1; }
        }

        private static IReadOnlyDictionary<string, object?> Fields(params (string Key, object? Value)[] pairs)
        {
            var d = new Dictionary<string, object?>(pairs.Length);
            foreach (var (k, v) in pairs) d[k] = v;
            return d;
        }
    }
}

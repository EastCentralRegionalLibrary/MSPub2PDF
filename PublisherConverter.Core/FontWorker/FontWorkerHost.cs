using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FontWorker
{
    /// <summary>
    /// Hosts the privileged font installer behind the font-worker IPC loop.
    /// Runs inside the elevated "--mode=font-worker" process and is the sole
    /// privilege boundary: it owns all machine-wide installation and contains
    /// no UI, conversion, or orchestration logic.
    ///
    /// Failure isolation: an individual installation failure is returned to the
    /// client as a structured failure response and the worker keeps serving.
    /// The loop only ends on a shutdown request, a closed pipe, or an
    /// unrecoverable transport error.
    /// </summary>
    public sealed class FontWorkerHost
    {
        public const int FatalExitCode = 2;

        private readonly IFontWorkerTransport _transport;
        private readonly IPrivilegedFontInstaller _installer;
        private readonly IStructuredLogger _logger;
        private readonly IPrivilegeChecker _privilegeChecker;
        private readonly bool _isElevated;

        public FontWorkerHost(
            IFontWorkerTransport transport,
            IPrivilegedFontInstaller installer,
            IStructuredLogger? logger = null,
            IPrivilegeChecker? privilegeChecker = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _installer = installer ?? throw new ArgumentNullException(nameof(installer));
            _logger = logger ?? NullStructuredLogger.Instance;
            _privilegeChecker = privilegeChecker ?? new StaticPrivilegeChecker(true);
            _isElevated = _privilegeChecker.IsCurrentProcessElevated();
        }

        /// <summary>
        /// Convenience factory for the production entry point: binds a named
        /// pipe server transport for <paramref name="pipeName"/>.
        /// </summary>
        public static FontWorkerHost CreateNamedPipeHost(
            string pipeName,
            IPrivilegedFontInstaller installer,
            IStructuredLogger? logger = null,
            IPrivilegeChecker? privilegeChecker = null)
        {
            return new FontWorkerHost(new NamedPipeFontWorkerTransport(pipeName, isServer: true), installer, logger, privilegeChecker);
        }

        /// <summary>
        /// Connects the transport and runs the request loop until shutdown.
        /// Returns normally on a clean shutdown / pipe close.
        /// </summary>
        public void Run()
        {
            try
            {
                _transport.Connect();
            }
            catch (Exception ex)
            {
                _logger.Error("worker.connect.failed", null, Fields(("error", ex.Message)));
                return;
            }

            _logger.Info("worker.started", null, Fields(("elevated", _isElevated), ("version", FontWorkerProtocol.Version)));
            if (!_isElevated)
            {
                // The launcher requests elevation; arriving here non-elevated
                // means the worker can't perform privileged installs. We still
                // serve so the client's health check can observe the state.
                _logger.Warn("worker.not_elevated", null);
            }

            try
            {
                while (true)
                {
                    FontWorkerRequest? request;
                    try
                    {
                        request = _transport.ReceiveRequestSync();
                    }
                    catch (Exception ex)
                    {
                        // Unrecoverable transport failure — the loop cannot continue.
                        _logger.Error("worker.receive.failed", null, Fields(("error", ex.Message)));
                        return;
                    }

                    if (request == null)
                    {
                        _logger.Info("worker.pipe_closed");
                        return;
                    }

                    if (HandleRequest(request)) return; // true => shutdown requested
                }
            }
            finally
            {
                _logger.Info("worker.stopped");
            }
        }

        /// <summary>Returns true when the worker should stop (shutdown).</summary>
        private bool HandleRequest(FontWorkerRequest request)
        {
            string correlationId = request.CorrelationId ?? string.Empty;
            _logger.Info("worker.request", correlationId, Fields(("type", request.Type.ToString())));

            // Protocol version guard — refuse a peer we can't understand rather
            // than mis-parsing its payloads.
            if (!string.Equals(request.ProtocolVersion, FontWorkerProtocol.Version, StringComparison.Ordinal))
            {
                TrySend(EnvelopeError(request, $"Unsupported protocol version '{request.ProtocolVersion}'."));
                return false;
            }

            switch (request.Type)
            {
                case FontWorkerMessageType.HealthCheck:
                    TrySend(new FontWorkerResponse
                    {
                        CorrelationId = correlationId,
                        Type = FontWorkerMessageType.HealthCheck,
                        HealthCheck = new HealthCheckResponse { Healthy = true, WorkerVersion = FontWorkerProtocol.Version, Elevated = _isElevated },
                    });
                    return false;

                case FontWorkerMessageType.Shutdown:
                    TrySend(new FontWorkerResponse
                    {
                        CorrelationId = correlationId,
                        Type = FontWorkerMessageType.Shutdown,
                        Shutdown = new ShutdownResponse { Acknowledged = true },
                    });
                    _logger.Info("worker.shutdown_requested", correlationId);
                    return true;

                case FontWorkerMessageType.InstallCapability:
                    TrySend(DispatchInstallCapability(request, correlationId));
                    return false;

                case FontWorkerMessageType.InstallCapabilitiesBatch:
                    TrySend(DispatchInstallCapabilitiesBatch(request, correlationId));
                    return false;

                case FontWorkerMessageType.InstallFont:
                    TrySend(DispatchInstallFont(request, correlationId));
                    return false;

                default:
                    TrySend(EnvelopeError(request, $"Unknown message type '{request.Type}'."));
                    return false;
            }
        }

        private FontWorkerResponse DispatchInstallCapability(FontWorkerRequest request, string correlationId)
        {
            if (request.InstallCapability == null)
            {
                return EnvelopeError(request, "InstallCapability payload was missing.");
            }

            // Failure isolation: any exception becomes a structured failure
            // response; the worker stays alive for the next request.
            try
            {
                var response = RunBlocking(ct => _installer.InstallCapabilityAsync(request.InstallCapability, correlationId, ct));
                _logger.Info("worker.response", correlationId, Fields(("type", "InstallCapability"), ("outcome", response.Outcome.ToString())));
                return new FontWorkerResponse { CorrelationId = correlationId, Type = FontWorkerMessageType.InstallCapability, InstallCapability = response };
            }
            catch (Exception ex)
            {
                _logger.Error("worker.handler.error", correlationId, Fields(("type", "InstallCapability"), ("error", ex.Message)));
                return new FontWorkerResponse
                {
                    CorrelationId = correlationId,
                    Type = FontWorkerMessageType.InstallCapability,
                    InstallCapability = InstallCapabilityResponse.Failed(request.InstallCapability.CapabilityName, ex.Message),
                };
            }
        }

        private FontWorkerResponse DispatchInstallCapabilitiesBatch(FontWorkerRequest request, string correlationId)
        {
            if (request.InstallCapabilitiesBatch == null)
            {
                return EnvelopeError(request, "InstallCapabilitiesBatch payload was missing.");
            }

            try
            {
                var response = RunBlocking(ct => _installer.InstallCapabilitiesBatchAsync(request.InstallCapabilitiesBatch, correlationId, ct));
                _logger.Info("worker.response", correlationId, Fields(("type", "InstallCapabilitiesBatch"), ("count", response.Results.Count)));
                return new FontWorkerResponse { CorrelationId = correlationId, Type = FontWorkerMessageType.InstallCapabilitiesBatch, InstallCapabilitiesBatch = response };
            }
            catch (Exception ex)
            {
                _logger.Error("worker.handler.error", correlationId, Fields(("type", "InstallCapabilitiesBatch"), ("error", ex.Message)));
                var failed = new List<InstallCapabilityResponse>();
                foreach (var name in request.InstallCapabilitiesBatch.CapabilityNames)
                {
                    failed.Add(InstallCapabilityResponse.Failed(name, ex.Message));
                }
                return new FontWorkerResponse
                {
                    CorrelationId = correlationId,
                    Type = FontWorkerMessageType.InstallCapabilitiesBatch,
                    InstallCapabilitiesBatch = new InstallCapabilitiesBatchResponse { Results = failed },
                };
            }
        }

        private FontWorkerResponse DispatchInstallFont(FontWorkerRequest request, string correlationId)
        {
            if (request.InstallFont == null)
            {
                return EnvelopeError(request, "InstallFont payload was missing.");
            }

            try
            {
                var response = RunBlocking(ct => _installer.InstallFontAsync(request.InstallFont, correlationId, ct));
                _logger.Info("worker.response", correlationId, Fields(("type", "InstallFont"), ("outcome", response.Outcome.ToString())));
                return new FontWorkerResponse { CorrelationId = correlationId, Type = FontWorkerMessageType.InstallFont, InstallFont = response };
            }
            catch (Exception ex)
            {
                _logger.Error("worker.handler.error", correlationId, Fields(("type", "InstallFont"), ("error", ex.Message)));
                return new FontWorkerResponse
                {
                    CorrelationId = correlationId,
                    Type = FontWorkerMessageType.InstallFont,
                    InstallFont = InstallFontResponse.Failed(request.InstallFont.FamilyName, ex.Message),
                };
            }
        }

        private static T RunBlocking<T>(Func<CancellationToken, Task<T>> operation)
        {
            // The host loop is synchronous (one request at a time); bridge to
            // the async installer without a captured sync context.
            return Task.Run(() => operation(CancellationToken.None)).GetAwaiter().GetResult();
        }

        private static FontWorkerResponse EnvelopeError(FontWorkerRequest request, string error)
        {
            return new FontWorkerResponse
            {
                CorrelationId = request.CorrelationId ?? string.Empty,
                Type = request.Type,
                EnvelopeSuccess = false,
                EnvelopeError = error,
            };
        }

        private void TrySend(FontWorkerResponse response)
        {
            try { _transport.SendResponseSync(response); }
            catch (Exception ex) { _logger.Error("worker.send.failed", response.CorrelationId, Fields(("error", ex.Message))); }
        }

        private static IReadOnlyDictionary<string, object?> Fields(params (string Key, object? Value)[] pairs)
        {
            var d = new Dictionary<string, object?>(pairs.Length);
            foreach (var (k, v) in pairs) d[k] = v;
            return d;
        }
    }
}

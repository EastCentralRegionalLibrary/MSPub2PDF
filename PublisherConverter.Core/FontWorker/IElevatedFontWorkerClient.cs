using System;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FontWorker
{
    /// <summary>
    /// Client-side handle to the session-scoped elevated font worker. Owns the
    /// worker process lifecycle and the IPC channel; exposes strongly-typed
    /// request methods and the worker's health state.
    ///
    /// Contract:
    ///   * <see cref="EnsureStartedAsync"/> launches the worker (at most one
    ///     live process at a time) and verifies readiness via a health check
    ///     within the configured startup timeout. It throws
    ///     <see cref="FontWorkerStartupException"/> if the worker cannot become
    ///     healthy — the caller then treats elevation as unavailable.
    ///   * Operation methods never auto-relaunch. If the worker is not healthy,
    ///     or a request times out / the channel breaks, they return a
    ///     structured failure response and mark the worker unhealthy. The
    ///     caller decides whether to launch a fresh worker for later work.
    ///
    /// Fully testable without elevation or Windows: every collaborator (process
    /// launcher, transport, health monitor) is injected.
    /// </summary>
    public interface IElevatedFontWorkerClient : IDisposable
    {
        /// <summary>True when a worker process is live and the channel is usable.</summary>
        bool IsHealthy { get; }

        /// <summary>
        /// Ensures a healthy worker is running and ready to accept requests.
        /// Idempotent — a no-op when the worker is already healthy. Throws
        /// <see cref="FontWorkerStartupException"/> on launch/connect/health
        /// failure.
        /// </summary>
        Task EnsureStartedAsync(CancellationToken cancellationToken);

        Task<HealthCheckResponse?> CheckHealthAsync(string correlationId, int? timeoutSeconds, CancellationToken cancellationToken);

        Task<InstallCapabilityResponse> InstallCapabilityAsync(InstallCapabilityRequest request, string correlationId, int? timeoutSeconds, CancellationToken cancellationToken);

        Task<InstallCapabilitiesBatchResponse> InstallCapabilitiesBatchAsync(InstallCapabilitiesBatchRequest request, string correlationId, int? timeoutSeconds, CancellationToken cancellationToken);

        Task<InstallFontResponse> InstallFontAsync(InstallFontRequest request, string correlationId, int? timeoutSeconds, CancellationToken cancellationToken);

        /// <summary>
        /// Sends a best-effort shutdown request and releases the worker. Safe to
        /// call when the worker is already gone.
        /// </summary>
        Task ShutdownAsync(CancellationToken cancellationToken);
    }
}

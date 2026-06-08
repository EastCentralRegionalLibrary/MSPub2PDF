using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PublisherConverter.Core.FontWorker
{
    /// <summary>
    /// Protocol-wide constants and serialization settings for the elevated
    /// font worker IPC channel. The protocol is deliberately kept independent
    /// of any transport (named pipe, in-memory loopback, …) — every message is
    /// a plain DTO that round-trips through <see cref="JsonOptions"/>.
    /// </summary>
    public static class FontWorkerProtocol
    {
        /// <summary>
        /// Wire-format version. Bumped when a breaking change is made to the
        /// envelope or payload shapes. Carried on every request/response so a
        /// newer main process talking to an older worker (or vice versa) can
        /// detect and reject an incompatible peer.
        /// </summary>
        public const string Version = "1.0";

        /// <summary>Command-line argument that selects the font-worker runtime mode.</summary>
        public const string FontWorkerModeArgument = "--mode=font-worker";

        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };

        /// <summary>Creates a fresh correlation identifier for a new request.</summary>
        public static string NewCorrelationId() => Guid.NewGuid().ToString("N");
    }

    /// <summary>Discriminates the payload carried by an envelope.</summary>
    public enum FontWorkerMessageType
    {
        HealthCheck,
        InstallCapability,
        InstallCapabilitiesBatch,
        InstallFont,
        Shutdown,
    }

    /// <summary>
    /// Outcome of a single privileged installation operation. Idempotent by
    /// design — re-installing an already-present resource reports
    /// <see cref="AlreadyPresent"/> rather than an error.
    /// </summary>
    public enum InstallOutcome
    {
        /// <summary>The resource was installed by this operation.</summary>
        Installed,

        /// <summary>The resource was already installed; the operation was a no-op.</summary>
        AlreadyPresent,

        /// <summary>The operation ran but could not install the resource.</summary>
        Failed,

        /// <summary>
        /// The operation is not supported in this environment (e.g. the worker
        /// is not running on a Windows edition that exposes the capability, or
        /// the request reached a non-Windows worker used only in tests).
        /// </summary>
        Unsupported,
    }

    public static class InstallOutcomeExtensions
    {
        /// <summary>
        /// True when the resource is present after the operation — i.e. it was
        /// installed now or was already there. Both count as "resolved".
        /// </summary>
        public static bool IsSuccess(this InstallOutcome outcome)
            => outcome == InstallOutcome.Installed || outcome == InstallOutcome.AlreadyPresent;
    }

    // -------------------------------------------------------------------------
    // Request / response payload DTOs. All are immutable (init-only) so a DTO
    // can never be mutated after it has been handed to the transport.
    // -------------------------------------------------------------------------

    public sealed class HealthCheckRequest
    {
    }

    public sealed class HealthCheckResponse
    {
        public bool Healthy { get; init; } = true;
        public string? WorkerVersion { get; init; }

        /// <summary>
        /// Whether the worker process is actually running elevated. Surfaced so
        /// the client can log a clear diagnostic if a worker that was launched
        /// with the "runas" verb somehow ended up non-elevated.
        /// </summary>
        public bool Elevated { get; init; }
    }

    public sealed class InstallCapabilityRequest
    {
        /// <summary>Full Windows capability name, e.g. "Language.Fonts.Hant~~~und-HANT~0.0.1.0".</summary>
        public string CapabilityName { get; init; } = string.Empty;
    }

    public sealed class InstallCapabilityResponse
    {
        public string CapabilityName { get; init; } = string.Empty;
        public InstallOutcome Outcome { get; init; }
        public string? Detail { get; init; }

        public static InstallCapabilityResponse Failed(string capabilityName, string detail)
            => new InstallCapabilityResponse { CapabilityName = capabilityName, Outcome = InstallOutcome.Failed, Detail = detail };
    }

    public sealed class InstallCapabilitiesBatchRequest
    {
        public IReadOnlyList<string> CapabilityNames { get; init; } = Array.Empty<string>();
    }

    public sealed class InstallCapabilitiesBatchResponse
    {
        public IReadOnlyList<InstallCapabilityResponse> Results { get; init; } = Array.Empty<InstallCapabilityResponse>();
    }

    public sealed class InstallFontRequest
    {
        /// <summary>Family name the font registers under (HKLM value name stem).</summary>
        public string FamilyName { get; init; } = string.Empty;

        /// <summary>
        /// Absolute path to the already-downloaded font file. The main process
        /// performs the (non-privileged) network download into a temp location
        /// and hands the elevated worker the local path to install system-wide.
        /// </summary>
        public string SourcePath { get; init; } = string.Empty;

        /// <summary>Desired file name inside the system fonts directory.</summary>
        public string FileName { get; init; } = string.Empty;
    }

    public sealed class InstallFontResponse
    {
        public string FamilyName { get; init; } = string.Empty;
        public InstallOutcome Outcome { get; init; }
        public string? Detail { get; init; }

        public static InstallFontResponse Failed(string familyName, string detail)
            => new InstallFontResponse { FamilyName = familyName, Outcome = InstallOutcome.Failed, Detail = detail };
    }

    public sealed class ShutdownRequest
    {
    }

    public sealed class ShutdownResponse
    {
        public bool Acknowledged { get; init; } = true;
    }

    // -------------------------------------------------------------------------
    // Envelopes. A single discriminated envelope keeps System.Text.Json
    // serialization simple (no polymorphic type resolver) while preserving the
    // strongly-typed, versionable, transport-independent message contract.
    // -------------------------------------------------------------------------

    public sealed class FontWorkerRequest
    {
        public string ProtocolVersion { get; init; } = FontWorkerProtocol.Version;
        public string CorrelationId { get; init; } = string.Empty;
        public FontWorkerMessageType Type { get; init; }

        public HealthCheckRequest? HealthCheck { get; init; }
        public InstallCapabilityRequest? InstallCapability { get; init; }
        public InstallCapabilitiesBatchRequest? InstallCapabilitiesBatch { get; init; }
        public InstallFontRequest? InstallFont { get; init; }
        public ShutdownRequest? Shutdown { get; init; }
    }

    public sealed class FontWorkerResponse
    {
        public string ProtocolVersion { get; init; } = FontWorkerProtocol.Version;
        public string CorrelationId { get; init; } = string.Empty;
        public FontWorkerMessageType Type { get; init; }

        /// <summary>
        /// False only when the worker could not dispatch the request at all
        /// (unknown message type, malformed envelope, protocol mismatch). A
        /// failed <em>installation</em> is still an EnvelopeSuccess=true
        /// response carrying an <see cref="InstallOutcome.Failed"/> payload —
        /// that distinction is what lets the worker stay alive and keep serving
        /// after an individual operation fails.
        /// </summary>
        public bool EnvelopeSuccess { get; init; } = true;
        public string? EnvelopeError { get; init; }

        public HealthCheckResponse? HealthCheck { get; init; }
        public InstallCapabilityResponse? InstallCapability { get; init; }
        public InstallCapabilitiesBatchResponse? InstallCapabilitiesBatch { get; init; }
        public InstallFontResponse? InstallFont { get; init; }
        public ShutdownResponse? Shutdown { get; init; }
    }
}

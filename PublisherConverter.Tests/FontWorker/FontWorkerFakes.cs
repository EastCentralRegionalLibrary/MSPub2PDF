using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core;
using PublisherConverter.Core.FontWorker;

namespace PublisherConverter.Tests.FontWorker
{
    /// <summary>Records launches and hands back fake process handles. Can be made to throw.</summary>
    public sealed class FakeFontWorkerProcessLauncher : IFontWorkerProcessLauncher
    {
        public List<string> RequestedPipeNames { get; } = new List<string>();
        public List<FakeProcessHandle> CreatedProcesses { get; } = new List<FakeProcessHandle>();
        public Exception? ThrowOnStart { get; set; }

        public IProcessHandle StartFontWorker(string pipeName)
        {
            RequestedPipeNames.Add(pipeName);
            if (ThrowOnStart != null) throw ThrowOnStart;
            var handle = new FakeProcessHandle(CreatedProcesses.Count + 1);
            CreatedProcesses.Add(handle);
            return handle;
        }
    }

    /// <summary>
    /// Client-side fake transport: replays queued responses and records every
    /// request sent. Server-side members are unused by client tests.
    /// </summary>
    public class FakeFontWorkerTransport : IFontWorkerTransport
    {
        public Queue<FontWorkerResponse> Responses { get; } = new Queue<FontWorkerResponse>();
        public List<FontWorkerRequest> SentRequests { get; } = new List<FontWorkerRequest>();
        public bool Connected { get; private set; }
        public bool Disposed { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken, int? timeoutMs = null)
        {
            Connected = true;
            return Task.CompletedTask;
        }

        public Task SendRequestAsync(FontWorkerRequest request, CancellationToken cancellationToken)
        {
            SentRequests.Add(request);
            return Task.CompletedTask;
        }

        public virtual Task<FontWorkerResponse> ReceiveResponseAsync(CancellationToken cancellationToken)
        {
            // Default: echo a successful response matching the last request's
            // type so happy-path tests don't have to enqueue every reply.
            if (Responses.Count > 0) return Task.FromResult(Responses.Dequeue());
            var last = SentRequests.LastOrDefault();
            return Task.FromResult(DefaultResponseFor(last));
        }

        public void Connect() => Connected = true;
        public FontWorkerRequest? ReceiveRequestSync() => throw new NotSupportedException();
        public void SendResponseSync(FontWorkerResponse response) => throw new NotSupportedException();
        public void Dispose() => Disposed = true;

        protected static FontWorkerResponse DefaultResponseFor(FontWorkerRequest? request)
        {
            var type = request?.Type ?? FontWorkerMessageType.HealthCheck;
            var corr = request?.CorrelationId ?? string.Empty;
            return type switch
            {
                FontWorkerMessageType.HealthCheck => new FontWorkerResponse { CorrelationId = corr, Type = type, HealthCheck = new HealthCheckResponse { Healthy = true, Elevated = true, WorkerVersion = FontWorkerProtocol.Version } },
                FontWorkerMessageType.InstallCapability => new FontWorkerResponse { CorrelationId = corr, Type = type, InstallCapability = new InstallCapabilityResponse { CapabilityName = request?.InstallCapability?.CapabilityName ?? "", Outcome = InstallOutcome.Installed } },
                FontWorkerMessageType.InstallCapabilitiesBatch => new FontWorkerResponse { CorrelationId = corr, Type = type, InstallCapabilitiesBatch = new InstallCapabilitiesBatchResponse { Results = (request?.InstallCapabilitiesBatch?.CapabilityNames ?? new List<string>()).Select(n => new InstallCapabilityResponse { CapabilityName = n, Outcome = InstallOutcome.Installed }).ToList() } },
                FontWorkerMessageType.InstallFont => new FontWorkerResponse { CorrelationId = corr, Type = type, InstallFont = new InstallFontResponse { FamilyName = request?.InstallFont?.FamilyName ?? "", Outcome = InstallOutcome.Installed } },
                FontWorkerMessageType.Shutdown => new FontWorkerResponse { CorrelationId = corr, Type = type, Shutdown = new ShutdownResponse { Acknowledged = true } },
                _ => new FontWorkerResponse { CorrelationId = corr, Type = type, EnvelopeSuccess = false, EnvelopeError = "unhandled" },
            };
        }
    }

    /// <summary>Privileged installer fake for host / loopback tests. Records calls and reports configured outcomes.</summary>
    public sealed class FakePrivilegedFontInstaller : IPrivilegedFontInstaller
    {
        public Dictionary<string, InstallOutcome> CapabilityOutcomes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, InstallOutcome> FontOutcomes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public InstallOutcome DefaultCapabilityOutcome { get; set; } = InstallOutcome.Installed;
        public InstallOutcome DefaultFontOutcome { get; set; } = InstallOutcome.Installed;

        public List<string> InstalledCapabilities { get; } = new();
        public List<string> InstalledFonts { get; } = new();
        public List<string> SeenCorrelationIds { get; } = new();

        public Func<string, Exception?>? ThrowForCapability { get; set; }

        public Task<InstallCapabilityResponse> InstallCapabilityAsync(InstallCapabilityRequest request, string correlationId, CancellationToken cancellationToken)
        {
            SeenCorrelationIds.Add(correlationId);
            InstalledCapabilities.Add(request.CapabilityName);
            var ex = ThrowForCapability?.Invoke(request.CapabilityName);
            if (ex != null) throw ex;
            var outcome = CapabilityOutcomes.TryGetValue(request.CapabilityName, out var o) ? o : DefaultCapabilityOutcome;
            return Task.FromResult(new InstallCapabilityResponse { CapabilityName = request.CapabilityName, Outcome = outcome, Detail = outcome.ToString() });
        }

        public async Task<InstallCapabilitiesBatchResponse> InstallCapabilitiesBatchAsync(InstallCapabilitiesBatchRequest request, string correlationId, CancellationToken cancellationToken)
        {
            var results = new List<InstallCapabilityResponse>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in request.CapabilityNames)
            {
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
                results.Add(await InstallCapabilityAsync(new InstallCapabilityRequest { CapabilityName = name }, correlationId, cancellationToken));
            }
            return new InstallCapabilitiesBatchResponse { Results = results };
        }

        public Task<InstallFontResponse> InstallFontAsync(InstallFontRequest request, string correlationId, CancellationToken cancellationToken)
        {
            SeenCorrelationIds.Add(correlationId);
            InstalledFonts.Add(request.FamilyName);
            var outcome = FontOutcomes.TryGetValue(request.FamilyName, out var o) ? o : DefaultFontOutcome;
            return Task.FromResult(new InstallFontResponse { FamilyName = request.FamilyName, Outcome = outcome, Detail = outcome.ToString() });
        }
    }

    /// <summary>
    /// Elevated worker client fake for FontManagementService tests. Tracks
    /// lifecycle calls and lets a test pin per-capability batch outcomes and
    /// startup behavior — no real process or transport.
    /// </summary>
    public sealed class FakeElevatedFontWorkerClient : IElevatedFontWorkerClient
    {
        public int EnsureStartedCount { get; private set; }
        public bool IsHealthy { get; set; } = true;
        public Exception? EnsureStartedException { get; set; }
        public bool ShutdownCalled { get; private set; }
        public bool Disposed { get; private set; }

        public Dictionary<string, InstallOutcome> CapabilityOutcomes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public InstallOutcome DefaultCapabilityOutcome { get; set; } = InstallOutcome.Installed;
        public Dictionary<string, InstallOutcome> FontOutcomes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public InstallOutcome DefaultFontOutcome { get; set; } = InstallOutcome.Installed;
        public List<InstallCapabilitiesBatchRequest> BatchRequests { get; } = new();
        public List<InstallFontRequest> FontRequests { get; } = new();
        public List<byte[]> FontPayloads { get; } = new();
        public List<string> SeenCorrelationIds { get; } = new();

        /// <summary>When set, the client reports unhealthy after the first batch call (worker died mid-cycle).</summary>
        public bool BecomeUnhealthyAfterBatch { get; set; }

        public Task EnsureStartedAsync(CancellationToken cancellationToken)
        {
            EnsureStartedCount++;
            if (EnsureStartedException != null)
            {
                IsHealthy = false;
                throw EnsureStartedException;
            }
            IsHealthy = true;
            return Task.CompletedTask;
        }

        public Task<HealthCheckResponse?> CheckHealthAsync(string correlationId, int? timeoutSeconds, CancellationToken cancellationToken)
            => Task.FromResult<HealthCheckResponse?>(new HealthCheckResponse { Healthy = IsHealthy, Elevated = true });

        public Task<InstallCapabilityResponse> InstallCapabilityAsync(InstallCapabilityRequest request, string correlationId, int? timeoutSeconds, CancellationToken cancellationToken)
        {
            SeenCorrelationIds.Add(correlationId);
            return Task.FromResult(new InstallCapabilityResponse { CapabilityName = request.CapabilityName, Outcome = OutcomeFor(request.CapabilityName) });
        }

        public Task<InstallCapabilitiesBatchResponse> InstallCapabilitiesBatchAsync(InstallCapabilitiesBatchRequest request, string correlationId, int? timeoutSeconds, CancellationToken cancellationToken)
        {
            SeenCorrelationIds.Add(correlationId);
            BatchRequests.Add(request);
            var results = request.CapabilityNames.Select(n => new InstallCapabilityResponse { CapabilityName = n, Outcome = OutcomeFor(n) }).ToList();
            if (BecomeUnhealthyAfterBatch) IsHealthy = false;
            return Task.FromResult(new InstallCapabilitiesBatchResponse { Results = results });
        }

        public Task<InstallFontResponse> InstallFontAsync(InstallFontRequest request, string correlationId, int? timeoutSeconds, CancellationToken cancellationToken)
        {
            SeenCorrelationIds.Add(correlationId);
            FontRequests.Add(request);
            // Snapshot the staged file content (the installer deletes it afterwards).
            try { if (File.Exists(request.SourcePath)) FontPayloads.Add(File.ReadAllBytes(request.SourcePath)); } catch { }
            var outcome = FontOutcomes.TryGetValue(request.FamilyName, out var o) ? o : DefaultFontOutcome;
            return Task.FromResult(new InstallFontResponse { FamilyName = request.FamilyName, Outcome = outcome });
        }

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            ShutdownCalled = true;
            IsHealthy = false;
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;

        private InstallOutcome OutcomeFor(string capability)
            => CapabilityOutcomes.TryGetValue(capability, out var o) ? o : DefaultCapabilityOutcome;
    }

    /// <summary>Simple mutable installed-font provider for availability-cache wiring in tests.</summary>
    public sealed class SetInstalledFontProvider : IRefreshableInstalledFontProvider
    {
        public HashSet<string> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int RefreshCount { get; private set; }

        public bool IsInstalled(string family) => Installed.Contains(FontNameNormalizer.Normalize(family));
        public void Refresh() => RefreshCount++;
    }

    /// <summary>A provisioning strategy whose success per font is configured up front. Records every attempt.</summary>
    public sealed class ScriptedStrategy : IFontProvisioningStrategy
    {
        private readonly Dictionary<string, bool> _outcomes;
        public string Name { get; }
        public List<string> Attempts { get; } = new();

        public ScriptedStrategy(string name, Dictionary<string, bool> outcomes)
        {
            Name = name;
            _outcomes = outcomes;
        }

        public bool CanResolve(string fontFamily) => _outcomes.ContainsKey(fontFamily);

        public Task<bool> TryResolveAsync(string fontFamily, IList<string> log, CancellationToken cancellationToken)
        {
            Attempts.Add(fontFamily);
            bool ok = _outcomes.TryGetValue(fontFamily, out var v) && v;
            log.Add($"[scripted:{Name}] {fontFamily} -> {ok}");
            return Task.FromResult(ok);
        }
    }
}

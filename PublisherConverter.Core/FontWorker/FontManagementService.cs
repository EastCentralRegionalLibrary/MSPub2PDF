using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FeaturesOnDemand;

namespace PublisherConverter.Core.FontWorker
{
    /// <summary>
    /// Session-scoped font provisioning coordinator and privileged-operation
    /// router. Implements <see cref="IFontResolver"/> so it drops into the
    /// existing orchestrator, and <see cref="IFontProvisioningSession"/> so the
    /// orchestrator can scope the elevated worker to a single batch.
    ///
    /// Responsibilities (centralized here, not spread through the codebase):
    ///   * Detect which missing fonts map to a privileged Windows capability vs
    ///     a non-privileged downloadable fallback.
    ///   * Decide whether elevation is required and launch the worker at most
    ///     once per cycle.
    ///   * Route capability installs through the worker as a single batch.
    ///   * Route downloadable installs to the configured installer (system-wide
    ///     via the worker when elevated, user-level otherwise).
    ///   * Never perform privileged installation in this (non-elevated) process.
    ///
    /// The actual per-font strategy walk, availability caching, and re-check are
    /// delegated to <see cref="FontResolver"/> so that logic lives in exactly
    /// one place — this service only adds the capability-batch and worker
    /// lifecycle on top.
    /// </summary>
    public sealed class FontManagementService : IFontResolver, IFontProvisioningSession, IDisposable
    {
        private readonly FontMappingTable _mappings;
        private readonly FontAvailabilityCache _cache;
        private readonly Func<IElevatedFontWorkerClient> _workerClientFactory;
        private readonly Func<IElevatedFontWorkerClient?, IReadOnlyList<IFontProvisioningStrategy>> _downloadStrategyFactory;
        private readonly IStructuredLogger _logger;
        private readonly int? _requestTimeoutSeconds;

        // Optional Features-on-Demand fallback. When wired, fonts that the
        // capability install and downloadable strategies could not resolve are
        // routed through the batched FoD pipeline as a last resort. The installer
        // factory mirrors _downloadStrategyFactory's install-sink selection so the
        // FoD install lands system-wide (via the worker) or user-level to match.
        private readonly IFeaturesOnDemandFontPipeline? _fodPipeline;
        private readonly Func<IElevatedFontWorkerClient?, IUserFontInstaller>? _fodInstallerFactory;
        private readonly FeaturesOnDemandOptions _fodOptions;

        private readonly object _cycleLock = new object();

        // Per-cycle state. Reset by BeginCycleAsync, torn down by EndCycleAsync.
        private FontProvisioningPolicy _policy = new FontProvisioningPolicy { AutomaticInstallEnabled = true, AllowElevatedInstall = false };
        private IElevatedFontWorkerClient? _workerClient;
        private bool _elevationUnavailableThisCycle;

        /// <param name="mappings">The font → capability / downloadable mapping table.</param>
        /// <param name="cache">Shared availability cache (also used by the auditor).</param>
        /// <param name="workerClientFactory">
        /// Creates a fresh elevated worker client for a cycle. The client owns
        /// the single worker process; this factory is invoked at most once per
        /// cycle (lazily, only when elevation is actually needed).
        /// </param>
        /// <param name="downloadStrategyFactory">
        /// Builds the downloadable-fallback strategies (Google Fonts, GitHub,
        /// direct URL). Called with the live worker client when installs should
        /// go system-wide through the worker, or null for user-level installs.
        /// </param>
        public FontManagementService(
            FontMappingTable mappings,
            FontAvailabilityCache cache,
            Func<IElevatedFontWorkerClient> workerClientFactory,
            Func<IElevatedFontWorkerClient?, IReadOnlyList<IFontProvisioningStrategy>> downloadStrategyFactory,
            IStructuredLogger? logger = null,
            int? requestTimeoutSeconds = null,
            IFeaturesOnDemandFontPipeline? fodPipeline = null,
            Func<IElevatedFontWorkerClient?, IUserFontInstaller>? fodInstallerFactory = null,
            FeaturesOnDemandOptions? fodOptions = null)
        {
            _mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _workerClientFactory = workerClientFactory ?? throw new ArgumentNullException(nameof(workerClientFactory));
            _downloadStrategyFactory = downloadStrategyFactory ?? throw new ArgumentNullException(nameof(downloadStrategyFactory));
            _logger = logger ?? NullStructuredLogger.Instance;
            _requestTimeoutSeconds = requestTimeoutSeconds;
            _fodPipeline = fodPipeline;
            _fodInstallerFactory = fodInstallerFactory;
            _fodOptions = fodOptions ?? FeaturesOnDemandOptions.Default;
        }

        public Task BeginCycleAsync(FontProvisioningPolicy policy, CancellationToken cancellationToken)
        {
            IElevatedFontWorkerClient? stale;
            lock (_cycleLock)
            {
                _policy = policy ?? FontProvisioningPolicy.DetectOnly;
                _elevationUnavailableThisCycle = false;
                stale = _workerClient;
                _workerClient = null;
            }
            stale?.Dispose();

            _logger.Info("cycle.begin", null, Fields(
                ("automaticInstall", _policy.AutomaticInstallEnabled),
                ("allowElevated", _policy.AllowElevatedInstall)));
            return Task.CompletedTask;
        }

        public async Task EndCycleAsync(CancellationToken cancellationToken)
        {
            IElevatedFontWorkerClient? client;
            lock (_cycleLock)
            {
                client = _workerClient;
                _workerClient = null;
            }

            if (client != null)
            {
                try { await client.ShutdownAsync(cancellationToken).ConfigureAwait(false); }
                catch { /* best-effort */ }
                finally { client.Dispose(); }
            }

            _logger.Info("cycle.end");
        }

        public async Task<FontResolutionOutcome> ResolveMissingFontsAsync(IReadOnlyList<string> missingFonts, CancellationToken cancellationToken)
        {
            var input = missingFonts ?? (IReadOnlyList<string>)Array.Empty<string>();

            FontProvisioningPolicy policy;
            lock (_cycleLock) { policy = _policy; }

            string correlationId = FontWorkerProtocol.NewCorrelationId();
            var log = new List<string>();

            if (!policy.AutomaticInstallEnabled)
            {
                log.Add($"Font provisioning disabled — detection only ({input.Count} missing).");
                _logger.Info("provision.detect_only", correlationId, Fields(("missing", input.Count)));
                return new FontResolutionOutcome
                {
                    InitiallyMissing = input,
                    Resolved = Array.Empty<string>(),
                    StillMissing = input,
                    Log = log,
                };
            }

            // Phase 1 — Detection. No installation happens here.
            var capabilityNeeds = new List<(string Font, string Capability)>();
            bool anyResolvableNeeded = false;
            foreach (var font in input)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_cache.IsInstalled(font)) continue;

                bool hasMapping = _mappings.HasAnyMappingFor(font);
                if (hasMapping) anyResolvableNeeded = true;
                if (_mappings.TryGetCapability(font, out var capability))
                {
                    capabilityNeeds.Add((font, capability));
                }
            }

            // Phase 2 — Elevation decision. Launch the worker at most once per
            // cycle, only when something actually needs installing and the user
            // permits elevated installs.
            bool workerAvailable = false;
            if (policy.AllowElevatedInstall && anyResolvableNeeded)
            {
                workerAvailable = await EnsureWorkerAsync(correlationId, log, cancellationToken).ConfigureAwait(false);
            }
            else if (!policy.AllowElevatedInstall)
            {
                log.Add("Elevated installs disabled — user-level installs only.");
            }

            // Phase 3a — Capability provisioning via a single batch through the worker.
            if (capabilityNeeds.Count > 0)
            {
                if (workerAvailable)
                {
                    await InstallCapabilitiesAsync(capabilityNeeds, correlationId, log, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    foreach (var (font, capability) in capabilityNeeds)
                    {
                        log.Add($"  • {font} — requires Windows capability '{capability}' (elevated install unavailable); leaving to fallbacks if any.");
                    }
                }
            }

            // Phase 3b — Delegate the remaining work (downloadable fallbacks,
            // ordered preference, cache skip / mark / refresh / re-check) to the
            // proven FontResolver. Capability successes are already marked in the
            // cache, so the resolver skips them as resolved.
            // Re-evaluate health: if the worker died servicing the capability
            // batch, downloadable installs fall back to user-level rather than
            // routing to a dead worker.
            IElevatedFontWorkerClient? clientForStrategies = null;
            lock (_cycleLock)
            {
                if (workerAvailable && !_elevationUnavailableThisCycle && _workerClient != null && _workerClient.IsHealthy)
                {
                    clientForStrategies = _workerClient;
                }
            }
            bool elevatedDownload = clientForStrategies != null;
            var downloadStrategies = _downloadStrategyFactory(clientForStrategies);
            var inner = new FontResolver(_cache, downloadStrategies);
            var innerOutcome = await inner.ResolveMissingFontsAsync(input, cancellationToken).ConfigureAwait(false);

            log.AddRange(innerOutcome.Log);

            var resolved = new List<string>(innerOutcome.Resolved);
            var stillMissing = new List<string>(innerOutcome.StillMissing);

            // Phase 3c — Features-on-Demand fallback. Anything the capability
            // install and downloadable strategies left unresolved is routed, as a
            // single batch, through the FoD pipeline (download + verify + extract
            // + install Microsoft LanguageFeatures font CABs).
            if (_fodPipeline != null && _fodInstallerFactory != null && stillMissing.Count > 0)
            {
                var fodResolved = await RunFeaturesOnDemandAsync(stillMissing, clientForStrategies, correlationId, log, cancellationToken).ConfigureAwait(false);
                if (fodResolved.Count > 0)
                {
                    foreach (var font in fodResolved) _cache.MarkInstalled(font);
                    resolved.AddRange(stillMissing.Where(f => fodResolved.Contains(f)));
                    stillMissing = stillMissing.Where(f => !fodResolved.Contains(f)).ToList();
                }
            }

            _logger.Info("provision.complete", correlationId, Fields(
                ("missing", input.Count),
                ("resolved", resolved.Count),
                ("stillMissing", stillMissing.Count),
                ("elevated", elevatedDownload)));

            return new FontResolutionOutcome
            {
                InitiallyMissing = input,
                Resolved = resolved,
                StillMissing = stillMissing,
                Log = log,
            };
        }

        private async Task<HashSet<string>> RunFeaturesOnDemandAsync(
            IReadOnlyList<string> stillMissing,
            IElevatedFontWorkerClient? worker,
            string correlationId,
            IList<string> log,
            CancellationToken cancellationToken)
        {
            var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var installer = _fodInstallerFactory!(worker);
                var result = await _fodPipeline!.RunAsync(stillMissing, installer, _fodOptions, correlationId, null, cancellationToken).ConfigureAwait(false);
                foreach (var line in result.Log) log.Add(line);
                foreach (var font in result.Resolved) resolved.Add(font);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // FoD is a best-effort last resort — never let it fault the batch.
                log.Add($"! Features-on-Demand fallback failed: {ex.Message}");
                _logger.Error("provision.fod_failed", correlationId, Fields(("error", ex.Message)));
            }
            return resolved;
        }

        private async Task<bool> EnsureWorkerAsync(string correlationId, IList<string> log, CancellationToken cancellationToken)
        {
            lock (_cycleLock)
            {
                if (_elevationUnavailableThisCycle) return false;
                if (_workerClient != null && _workerClient.IsHealthy) return true;
                if (_workerClient == null)
                {
                    _workerClient = _workerClientFactory();
                }
            }

            IElevatedFontWorkerClient client;
            lock (_cycleLock) { client = _workerClient!; }

            try
            {
                await client.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
                log.Add("Elevated font worker ready (single elevation prompt for this run).");
                return true;
            }
            catch (FontWorkerStartupException ex)
            {
                // No second prompt this cycle: elevation is unavailable from here on.
                lock (_cycleLock)
                {
                    _elevationUnavailableThisCycle = true;
                    _workerClient?.Dispose();
                    _workerClient = null;
                }
                log.Add($"! Elevated font worker unavailable: {ex.Message}. Continuing with user-level installs only.");
                _logger.Error("provision.worker_unavailable", correlationId, Fields(("error", ex.Message)));
                return false;
            }
        }

        private async Task InstallCapabilitiesAsync(
            List<(string Font, string Capability)> capabilityNeeds,
            string correlationId,
            IList<string> log,
            CancellationToken cancellationToken)
        {
            IElevatedFontWorkerClient? client;
            lock (_cycleLock) { client = _workerClient; }
            if (client == null) return;

            var distinctCapabilities = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, capability) in capabilityNeeds)
            {
                if (seen.Add(capability)) distinctCapabilities.Add(capability);
            }

            log.Add($"Installing {distinctCapabilities.Count} Windows capability(ies) via elevated worker (batch).");
            var response = await client.InstallCapabilitiesBatchAsync(
                new InstallCapabilitiesBatchRequest { CapabilityNames = distinctCapabilities },
                correlationId,
                _requestTimeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            var succeeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var result in response.Results)
            {
                if (result.Outcome.IsSuccess()) succeeded.Add(result.CapabilityName);
                log.Add($"  capability {result.CapabilityName}: {result.Outcome}{(string.IsNullOrEmpty(result.Detail) ? "" : $" — {result.Detail}")}");
            }

            // Mark every font whose capability is now present as available so the
            // downstream FontResolver treats it as resolved.
            foreach (var (font, capability) in capabilityNeeds)
            {
                if (succeeded.Contains(capability))
                {
                    _cache.MarkInstalled(font);
                }
            }

            // If the worker died servicing the batch, don't prompt again this cycle.
            if (!client.IsHealthy)
            {
                lock (_cycleLock) { _elevationUnavailableThisCycle = true; }
                _logger.Warn("provision.worker_lost", correlationId);
                log.Add("! Elevated worker became unavailable during capability install; remaining work uses user-level installs.");
            }
        }

        public void Dispose()
        {
            IElevatedFontWorkerClient? client;
            lock (_cycleLock)
            {
                client = _workerClient;
                _workerClient = null;
            }
            client?.Dispose();
        }

        private static IReadOnlyDictionary<string, object?> Fields(params (string Key, object? Value)[] pairs)
        {
            var d = new Dictionary<string, object?>(pairs.Length);
            foreach (var (k, v) in pairs) d[k] = v;
            return d;
        }
    }
}

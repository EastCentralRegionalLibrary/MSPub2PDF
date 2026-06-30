using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FontWorker;

namespace PublisherConverter.Core
{
    using PublisherConverter.Core.FontSources;

    /// <summary>
    /// Top-level font acquisition coordinator. Implements the existing
    /// <see cref="IFontResolver"/>/<see cref="IFontProvisioningSession"/> contract
    /// so it drops straight into the converter, while internally running the
    /// deterministic fallback chain:
    ///
    ///   Layer 1 (batch): local cache + Windows capability + Features on Demand —
    ///       delegated to the existing Microsoft batch resolver so its elevation
    ///       and single-prompt batching are preserved.
    ///   Layers 2-4 (per still-missing font, stop on first success): local scratch,
    ///       Google Fonts, smart-routed vendor repos, then community sources.
    ///
    /// Batch-level error isolation is preserved — one font that exhausts every
    /// layer never affects the rest — and each attempt yields a structured
    /// <see cref="FontAcquisitionResult"/> exposed on <see cref="LastResults"/>.
    /// </summary>
    public sealed class FontSourceOrchestrator : IFontResolver, IFontProvisioningSession, IDisposable
    {
        private readonly IFontResolver _microsoftLayer;
        private readonly IReadOnlyList<IFontSourceResolver> _remoteResolvers;
        private readonly FontFamilyNormalizer _normalizer;
        private readonly FontAvailabilityCache _cache;
        private readonly Func<IUserFontInstaller> _installerFactory;
        private readonly FontSourceConfiguration _config;
        private readonly IStructuredLogger _logger;
        private readonly Func<string> _scratchRootProvider;
        private readonly LicenseApprovalCallback? _licenseApprovalCallback;

        private readonly object _stateLock = new object();
        private bool _automaticInstallEnabled;
        private string? _scratchDirectory;
        private List<FontAcquisitionResult> _lastResults = new List<FontAcquisitionResult>();

        public FontSourceOrchestrator(
            IFontResolver microsoftLayer,
            IReadOnlyList<IFontSourceResolver> remoteResolvers,
            FontFamilyNormalizer normalizer,
            FontAvailabilityCache cache,
            Func<IUserFontInstaller> installerFactory,
            FontSourceConfiguration config,
            IStructuredLogger? logger = null,
            Func<string>? scratchRootProvider = null,
            LicenseApprovalCallback? licenseApprovalCallback = null)
        {
            _microsoftLayer = microsoftLayer ?? throw new ArgumentNullException(nameof(microsoftLayer));
            _remoteResolvers = remoteResolvers ?? throw new ArgumentNullException(nameof(remoteResolvers));
            _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _installerFactory = installerFactory ?? throw new ArgumentNullException(nameof(installerFactory));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? NullStructuredLogger.Instance;
            _scratchRootProvider = scratchRootProvider ?? DefaultScratchRoot;
            _licenseApprovalCallback = licenseApprovalCallback;
        }

        /// <summary>Structured per-font results from the most recent resolve call.</summary>
        public IReadOnlyList<FontAcquisitionResult> LastResults
        {
            get { lock (_stateLock) return _lastResults.ToList(); }
        }

        public async Task BeginCycleAsync(FontProvisioningPolicy policy, CancellationToken cancellationToken)
        {
            lock (_stateLock)
            {
                _automaticInstallEnabled = policy?.AutomaticInstallEnabled ?? false;
                _scratchDirectory = Path.Combine(_scratchRootProvider(), $"cycle-{Guid.NewGuid():N}");
                _lastResults = new List<FontAcquisitionResult>();
            }
            if (_microsoftLayer is IFontProvisioningSession session)
            {
                await session.BeginCycleAsync(policy ?? FontProvisioningPolicy.DetectOnly, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task EndCycleAsync(CancellationToken cancellationToken)
        {
            if (_microsoftLayer is IFontProvisioningSession session)
            {
                await session.EndCycleAsync(cancellationToken).ConfigureAwait(false);
            }
            string? scratch;
            lock (_stateLock) { scratch = _scratchDirectory; _scratchDirectory = null; }
            TryDeleteDirectory(scratch);
        }

        public async Task<FontResolutionOutcome> ResolveMissingFontsAsync(IReadOnlyList<string> missingFonts, CancellationToken cancellationToken)
        {
            var input = missingFonts ?? (IReadOnlyList<string>)Array.Empty<string>();
            var log = new List<string>();
            string correlationId = FontWorkerProtocol.NewCorrelationId();
            var results = new List<FontAcquisitionResult>();

            // Snapshot which fonts were already present before the Microsoft layer
            // runs, so a cross-file cache hit is attributed to the local cache
            // rather than mis-reported as a fresh Features-on-Demand install.
            var preInstalled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var font in input)
            {
                if (_cache.IsInstalled(font)) preInstalled.Add(font);
            }

            // ---- Layer 1 (batch): Microsoft-owned + local cache ----
            FontResolutionOutcome inner;
            try
            {
                inner = await _microsoftLayer.ResolveMissingFontsAsync(input, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                log.Add($"Microsoft layer failed: {ex.Message}");
                inner = new FontResolutionOutcome { InitiallyMissing = input, StillMissing = input };
            }
            log.AddRange(inner.Log);
            foreach (var font in inner.Resolved)
            {
                results.Add(MicrosoftResult(font, preInstalled.Contains(font)));
            }

            var resolved = new List<string>(inner.Resolved);
            var stillMissing = new List<string>(inner.StillMissing);

            bool autoEnabled;
            string scratch;
            lock (_stateLock)
            {
                autoEnabled = _automaticInstallEnabled;
                scratch = _scratchDirectory ?? Path.Combine(_scratchRootProvider(), "adhoc");
            }

            // ---- Layers 2-4 (per font, sequential, stop on first success) ----
            if (autoEnabled && stillMissing.Count > 0)
            {
                var installer = _installerFactory();
                var context = new ResolverContext
                {
                    Installer = installer,
                    ScratchDirectory = scratch,
                    Log = log,
                    CorrelationId = correlationId,
                    Logger = _logger,
                };

                var nowResolved = new List<string>();
                foreach (var fontName in stillMissing)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = await ResolveOneAsync(fontName, context, cancellationToken).ConfigureAwait(false);
                    int resultIndex = results.Count;
                    results.Add(result);

                    if (result.IsResolved)
                    {
                        _cache.MarkInstalled(result.NormalizedFamily);
                        _cache.MarkInstalled(fontName);
                        nowResolved.Add(fontName);
                        log.Add($"  ✓ {fontName} resolved via {result.Layer}/{result.SourceId} (confidence {result.MatchConfidence:F2}).");
                    }
                    else if (result.ManualReviewRequired && result.DownloadedFilePath != null)
                    {
                        bool approved = await RequestLicenseApprovalAsync(fontName, result, log, cancellationToken).ConfigureAwait(false);
                        if (!approved)
                        {
                            log.Add($"  ⚠ {fontName}: candidate found but license not approved — skipping.");
                        }
                        else if (await InstallApprovedAsync(installer, fontName, result, log, cancellationToken).ConfigureAwait(false))
                        {
                            nowResolved.Add(fontName);
                            // Reflect the post-approval install in the structured results.
                            results[resultIndex] = AsInstalled(result);
                            log.Add($"  ✓ {fontName} resolved after user approved license (via {result.Layer}/{result.SourceId}).");
                        }
                        else
                        {
                            log.Add($"  ⚠ {fontName}: license approved but install failed — not resolved.");
                        }
                    }
                    else if (result.ManualReviewRequired)
                    {
                        log.Add($"  ⚠ {fontName}: candidate found via {result.Layer}/{result.SourceId} but license needs manual review — not installed (no staged file).");
                    }
                    else
                    {
                        log.Add($"  × {fontName}: no installable .ttf found across all layers.");
                    }
                }

                if (nowResolved.Count > 0)
                {
                    resolved.AddRange(nowResolved);
                    stillMissing = stillMissing.Where(f => !nowResolved.Contains(f)).ToList();
                    try { _cache.RefreshProvider(); } catch { }
                }
            }

            log.Add($"Font acquisition: resolved {resolved.Count}, still missing {stillMissing.Count}.");
            lock (_stateLock) { _lastResults = results; }

            return new FontResolutionOutcome
            {
                InitiallyMissing = input,
                Resolved = resolved,
                StillMissing = stillMissing,
                Log = log,
            };
        }

        private async Task<FontAcquisitionResult> ResolveOneAsync(string fontName, ResolverContext context, CancellationToken cancellationToken)
        {
            FontRequest request = _normalizer.Parse(fontName);
            FontAcquisitionResult? lastNonMissing = null;

            foreach (var resolver in _remoteResolvers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!resolver.IsEnabled) continue;

                FontAcquisitionResult result;
                try
                {
                    result = await resolver.TryResolveAsync(request, context, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // No network/parse failure escapes the resolver boundary.
                    context.Log.Add($"    [debug] {resolver.Layer}: threw {ex.GetType().Name}: {ex.Message}");
                    _logger.Log(StructuredLogLevel.Debug, "fontsource.resolver.threw", context.CorrelationId,
                        new Dictionary<string, object?> { ["layer"] = resolver.Layer.ToString(), ["error"] = ex.Message });
                    continue;
                }

                if (result.IsResolved) return result;
                if (result.Status != AcquisitionStatus.Missing) lastNonMissing = result; // manual-review / rejected
            }

            // Nothing installed: surface a manual-review/rejected candidate if any, else a clean miss.
            return lastNonMissing ?? new FontAcquisitionResult
            {
                RequestedFontName = request.RequestedName,
                NormalizedFamily = request.NormalizedFamily,
                RequestedStyle = FontFamilyNormalizer.StyleFileToken(request.RequestedStyles),
                Status = AcquisitionStatus.Missing,
                Layer = ResolutionLayer.None,
                FailureReason = "exhausted all layers",
            };
        }

        // Surfaces the staged candidate to the user. No callback wired → declined
        // (the safe default for an unclear license).
        private async Task<bool> RequestLicenseApprovalAsync(string fontName, FontAcquisitionResult result, IList<string> log, CancellationToken cancellationToken)
        {
            if (_licenseApprovalCallback == null) return false;

            var request = new LicenseApprovalRequest
            {
                FontName = result.RequestedFontName,
                SourceId = result.SourceId ?? result.Layer.ToString(),
                SourceUrl = result.SourceUrl,
                LicenseReason = result.FailureReason ?? "license requires manual review",
                LicenseText = result.LicenseText,
                StagedFilePath = result.DownloadedFilePath!,
            };

            try
            {
                return await _licenseApprovalCallback(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.Add($"  ! license approval callback threw for {fontName}: {ex.Message}");
                return false;
            }
        }

        // Installs an approved candidate from its staged scratch file and marks
        // the cache so a later file treats the font as resolved.
        private async Task<bool> InstallApprovedAsync(IUserFontInstaller installer, string fontName, FontAcquisitionResult result, IList<string> log, CancellationToken cancellationToken)
        {
            try
            {
                using var fs = File.OpenRead(result.DownloadedFilePath!);
                bool installed = await installer.InstallFromStreamAsync(
                    result.NormalizedFamily,
                    Path.GetFileName(result.DownloadedFilePath!),
                    fs, log, cancellationToken).ConfigureAwait(false);

                if (installed)
                {
                    _cache.MarkInstalled(result.NormalizedFamily);
                    _cache.MarkInstalled(fontName);
                }
                return installed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.Add($"    ! post-approval install failed for {fontName}: {ex.Message}");
                return false;
            }
        }

        // A post-approval install turns a ManualReviewRequired result into an
        // Installed one so LastResults agrees with the returned Resolved list.
        private static FontAcquisitionResult AsInstalled(FontAcquisitionResult r)
            => new FontAcquisitionResult
            {
                RequestedFontName = r.RequestedFontName,
                NormalizedFamily = r.NormalizedFamily,
                RequestedStyle = r.RequestedStyle,
                Status = AcquisitionStatus.Installed,
                Layer = r.Layer,
                SourceId = r.SourceId,
                SourceUrl = r.SourceUrl,
                DownloadedFilePath = r.DownloadedFilePath,
                InstalledFilePath = r.DownloadedFilePath,
                License = r.License,
                MatchConfidence = r.MatchConfidence,
                LicenseText = r.LicenseText,
                ManualReviewRequired = false,
            };

        private static FontAcquisitionResult MicrosoftResult(string font, bool wasCacheHit)
            => new FontAcquisitionResult
            {
                RequestedFontName = font,
                NormalizedFamily = FontNameNormalizer.Normalize(font),
                Status = AcquisitionStatus.Installed,
                Layer = wasCacheHit ? ResolutionLayer.LocalCache : ResolutionLayer.FeaturesOnDemand,
                SourceId = wasCacheHit ? "local-cache" : "microsoft",
                License = LicenseStatus.NotApplicable,
                MatchConfidence = 1.0,
            };

        public void Dispose()
        {
            (_microsoftLayer as IDisposable)?.Dispose();
        }

        private static string DefaultScratchRoot()
            => Path.Combine(Path.GetTempPath(), "MSPub2PDF", "fontsources");

        private static void TryDeleteDirectory(string? dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}

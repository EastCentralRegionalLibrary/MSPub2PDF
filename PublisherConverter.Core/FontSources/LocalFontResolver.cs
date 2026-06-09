using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FontWorker;

namespace PublisherConverter.Core.FontSources
{
    /// <summary>
    /// Layer 1 (local) — the zero-network short-circuit. Confirms a family is
    /// already installed (availability cache) or reuses a previously downloaded,
    /// already-validated <c>.ttf</c> from this session's scratch area. The
    /// Microsoft-owned part of Layer 1 (Windows capability + Features on Demand)
    /// is handled by the existing batch resolver the orchestrator runs first; this
    /// resolver covers cache + scratch so no remote layer is touched when the font
    /// is effectively already in hand.
    /// </summary>
    public sealed class LocalFontResolver : IFontSourceResolver
    {
        private readonly FontAvailabilityCache _cache;
        private readonly FontSourceConfiguration _config;
        private readonly IStructuredLogger _logger;

        public LocalFontResolver(FontAvailabilityCache cache, FontSourceConfiguration config, IStructuredLogger? logger = null)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? NullStructuredLogger.Instance;
        }

        public ResolutionLayer Layer => ResolutionLayer.LocalCache;

        public bool IsEnabled => _config.IsLayerEnabled(ResolutionLayer.LocalCache) || _config.IsLayerEnabled(ResolutionLayer.Scratch);

        public async Task<FontAcquisitionResult> TryResolveAsync(FontRequest request, ResolverContext context, CancellationToken cancellationToken)
        {
            if (_config.IsLayerEnabled(ResolutionLayer.LocalCache) && _cache.IsInstalled(request.NormalizedFamily))
            {
                return Resolved(request, ResolutionLayer.LocalCache, null, null);
            }

            if (_config.IsLayerEnabled(ResolutionLayer.Scratch))
            {
                var scratch = await TryScratchAsync(request, context, cancellationToken).ConfigureAwait(false);
                if (scratch != null) return scratch;
            }

            return FontAcquisitionResult.Miss(request, ResolutionLayer.LocalCache, "not in local cache or scratch");
        }

        private async Task<FontAcquisitionResult?> TryScratchAsync(FontRequest request, ResolverContext context, CancellationToken cancellationToken)
        {
            string dir = context.ScratchDirectory;
            if (!Directory.Exists(dir)) return null;

            string slug = UrlTemplate.Slug(request.NormalizedFamily);
            string[] candidates;
            try
            {
                candidates = Directory.GetFiles(dir, "*.ttf", SearchOption.TopDirectoryOnly)
                    .Where(p => UrlTemplate.Slug(Path.GetFileNameWithoutExtension(p)).StartsWith(slug, StringComparison.Ordinal))
                    .ToArray();
            }
            catch
            {
                return null;
            }

            foreach (var path in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] bytes;
                try { bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false); }
                catch { continue; }

                if (!FontIntegrityValidator.Validate(bytes).IsValidTtf) continue;

                bool installed;
                try
                {
                    using var fs = File.OpenRead(path);
                    installed = await context.Installer.InstallFromStreamAsync(request.NormalizedFamily, Path.GetFileName(path), fs, context.Log, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch { continue; }

                if (installed)
                {
                    _logger.Log(StructuredLogLevel.Debug, "fontsource.scratch.hit", context.CorrelationId, null);
                    return Resolved(request, ResolutionLayer.Scratch, path, path);
                }
            }
            return null;
        }

        private FontAcquisitionResult Resolved(FontRequest request, ResolutionLayer layer, string? downloaded, string? installed)
            => new FontAcquisitionResult
            {
                RequestedFontName = request.RequestedName,
                NormalizedFamily = request.NormalizedFamily,
                RequestedStyle = FontFamilyNormalizer.StyleFileToken(request.RequestedStyles),
                Status = installed != null ? AcquisitionStatus.Installed : AcquisitionStatus.Acquired,
                Layer = layer,
                SourceId = layer == ResolutionLayer.Scratch ? "scratch" : "local-cache",
                DownloadedFilePath = downloaded,
                InstalledFilePath = installed,
                License = LicenseStatus.NotApplicable,
                MatchConfidence = 1.0,
            };
    }
}

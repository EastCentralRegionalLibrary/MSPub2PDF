using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FontWorker;

namespace PublisherConverter.Core.FontSources
{
    /// <summary>
    /// Layer 2 — resolves open-source families from the repository-backed
    /// <c>google/fonts</c> index instead of scraping the CSS endpoint, so only
    /// authoritative static <c>.ttf</c> assets are acquired. The family's license
    /// sub-directory (ofl/apache/ufl) and the file-name layout come from config;
    /// a fast HEAD probe locates the family before anything is downloaded, and a
    /// family that exists only as a variable/webfont is treated as a miss.
    /// </summary>
    public sealed class GoogleFontsResolver : FontSourceResolverBase
    {
        private readonly FontSourceConfiguration _config;
        private readonly IFontHttpClient _http;

        public GoogleFontsResolver(FontSourceConfiguration config, IFontHttpClient http, FontLicenseEvaluator license, IStructuredLogger? logger = null)
            : base(license, logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public override ResolutionLayer Layer => ResolutionLayer.GoogleFonts;

        public override bool IsEnabled => _config.IsLayerEnabled(ResolutionLayer.GoogleFonts) && _config.Enabled(FontSourceType.GoogleFonts).Any();

        public override async Task<FontAcquisitionResult> TryResolveAsync(FontRequest request, ResolverContext context, CancellationToken cancellationToken)
        {
            TimeSpan probeTimeout = TimeSpan.FromMilliseconds(_config.Policy.ProbeTimeoutMs);
            TimeSpan downloadTimeout = TimeSpan.FromMilliseconds(_config.Policy.DownloadTimeoutMs);

            foreach (var source in _config.Enabled(FontSourceType.GoogleFonts))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await TryFromSourceAsync(source, request, context, probeTimeout, downloadTimeout, cancellationToken).ConfigureAwait(false);
                if (result.IsResolved) return result;
            }
            return FontAcquisitionResult.Miss(request, Layer, "no Google Fonts match");
        }

        private async Task<FontAcquisitionResult> TryFromSourceAsync(
            FontSourceDefinition source, FontRequest request, ResolverContext context,
            TimeSpan probeTimeout, TimeSpan downloadTimeout, CancellationToken cancellationToken)
        {
            // Always try Regular first to pin the family's license directory and
            // working path template, then re-use them for the other styles.
            string requestedToken = FontFamilyNormalizer.StyleFileToken(request.RequestedStyles);
            var styles = BuildStyleList(source, requestedToken);

            string? licenseDir = null;
            string? template = null;
            FontAcquisitionResult? primary = null;

            foreach (var style in styles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var located = await LocateAsync(source, request.NormalizedFamily, style, licenseDir, template, probeTimeout, context, cancellationToken).ConfigureAwait(false);
                if (located == null)
                {
                    // If we cannot even find Regular, the family is not here — bail fast.
                    if (style == "Regular" && licenseDir == null) return FontAcquisitionResult.Miss(request, Layer, "family not found in google/fonts");
                    continue;
                }

                licenseDir ??= located.Value.licenseDir;
                template ??= located.Value.template;

                byte[]? bytes = await _http.DownloadBytesAsync(located.Value.url, null, MaxTtfBytes, downloadTimeout, cancellationToken).ConfigureAwait(false);
                if (bytes == null)
                {
                    Debug(context, $"google: download failed for {located.Value.url}");
                    continue;
                }

                var license = LicenseEvaluator.Evaluate(source.LicenseHint, null, trustedSource: true);
                var outcome = await FinalizeTtfAsync(request, source.Id, located.Value.url, bytes, license, 0.95, style, context, cancellationToken).ConfigureAwait(false);

                // The primary result is the requested style, else the first installed.
                if (outcome.IsResolved && (primary == null || string.Equals(style, requestedToken, StringComparison.OrdinalIgnoreCase)))
                {
                    primary = outcome;
                }
            }

            return primary ?? FontAcquisitionResult.Miss(request, Layer, "no installable static .ttf found");
        }

        private async Task<(string url, string licenseDir, string template)?> LocateAsync(
            FontSourceDefinition source, string family, string style,
            string? knownLicenseDir, string? knownTemplate,
            TimeSpan probeTimeout, ResolverContext context, CancellationToken cancellationToken)
        {
            var licenseDirs = knownLicenseDir != null ? new[] { knownLicenseDir } : source.LicenseDirs.ToArray();
            var templates = knownTemplate != null ? new[] { knownTemplate } : source.PathTemplates.ToArray();
            var repo = source.Repo!;

            foreach (var dir in licenseDirs)
            {
                foreach (var tmpl in templates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var values = UrlTemplate.Values(family, style, dir, repo);
                    string path = UrlTemplate.Expand(tmpl, values);
                    string url = $"{source.RawBaseUrl.TrimEnd('/')}/{repo.Owner}/{repo.Repo}/{repo.Branch}/{path.TrimStart('/')}";

                    var probe = await _http.ProbeAsync(url, null, probeTimeout, cancellationToken).ConfigureAwait(false);
                    if (probe.Exists) return (url, dir, tmpl);
                }
            }
            return null;
        }

        private static List<string> BuildStyleList(FontSourceDefinition source, string requestedToken)
        {
            var styles = new List<string> { "Regular" };
            foreach (var s in source.Styles)
            {
                if (!styles.Contains(s, StringComparer.OrdinalIgnoreCase)) styles.Add(s);
            }
            if (!styles.Contains(requestedToken, StringComparer.OrdinalIgnoreCase)) styles.Add(requestedToken);
            return styles;
        }

        private const long MaxTtfBytes = 20 * 1024 * 1024;
    }
}

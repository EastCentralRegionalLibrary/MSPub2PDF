using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FeaturesOnDemand;
using PublisherConverter.Core.FontWorker;

namespace PublisherConverter.Core.FontSources
{
    /// <summary>
    /// Layer 3 — routes a family to a configured official vendor upstream (IBM
    /// Plex, JetBrains, Adobe Source, …) using the regex routing rules in
    /// FontSources.json, then acquires its .ttf either from a raw path template or
    /// from a tagged release archive. Unrelated repositories are never probed: a
    /// family that no route matches is an immediate miss.
    /// </summary>
    public sealed class VendorRepoResolver : FontSourceResolverBase
    {
        private const long MaxTtfBytes = 25 * 1024 * 1024;
        private const long MaxArchiveBytes = 200 * 1024 * 1024;

        private readonly FontSourceConfiguration _config;
        private readonly IFontHttpClient _http;
        private readonly FontArchiveInspector _archiveInspector;

        public VendorRepoResolver(
            FontSourceConfiguration config, IFontHttpClient http,
            FontArchiveInspector archiveInspector, FontLicenseEvaluator license, IStructuredLogger? logger = null)
            : base(license, logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _archiveInspector = archiveInspector ?? throw new ArgumentNullException(nameof(archiveInspector));
        }

        public override ResolutionLayer Layer => ResolutionLayer.VendorRepo;
        public override bool IsEnabled => _config.IsLayerEnabled(ResolutionLayer.VendorRepo) && _config.VendorRoutes.Count > 0;

        public override async Task<FontAcquisitionResult> TryResolveAsync(FontRequest request, ResolverContext context, CancellationToken cancellationToken)
        {
            var route = _config.RouteVendor(request.NormalizedFamily);
            if (route == null)
            {
                Debug(context, $"vendor: no route matched '{request.NormalizedFamily}'");
                return FontAcquisitionResult.Miss(request, Layer, "no vendor route matched");
            }

            var source = route.Source;
            Debug(context, $"vendor: '{request.NormalizedFamily}' routed to '{source.Id}'");

            TimeSpan probeTimeout = TimeSpan.FromMilliseconds(source.TimeoutOverrideMs ?? _config.Policy.ProbeTimeoutMs);
            TimeSpan downloadTimeout = TimeSpan.FromMilliseconds(_config.Policy.DownloadTimeoutMs);

            if (source.PathTemplates.Count > 0)
            {
                var raw = await TryRawPathsAsync(source, request, context, probeTimeout, downloadTimeout, cancellationToken).ConfigureAwait(false);
                if (raw.IsResolved) return raw;
            }

            if (!string.IsNullOrWhiteSpace(source.Repo?.ReleaseAssetPattern))
            {
                var release = await TryReleaseArchiveAsync(source, request, context, downloadTimeout, cancellationToken).ConfigureAwait(false);
                if (release.IsResolved) return release;
            }

            return FontAcquisitionResult.Miss(request, Layer, $"no .ttf found at vendor '{source.Id}'");
        }

        private async Task<FontAcquisitionResult> TryRawPathsAsync(
            FontSourceDefinition source, FontRequest request, ResolverContext context,
            TimeSpan probeTimeout, TimeSpan downloadTimeout, CancellationToken cancellationToken)
        {
            string requestedToken = FontFamilyNormalizer.StyleFileToken(request.RequestedStyles);
            var styles = new List<string> { "Regular" };
            foreach (var s in source.Styles) if (!styles.Contains(s, StringComparer.OrdinalIgnoreCase)) styles.Add(s);
            if (!styles.Contains(requestedToken, StringComparer.OrdinalIgnoreCase)) styles.Add(requestedToken);

            var repo = source.Repo!;
            FontAcquisitionResult? primary = null;

            foreach (var style in styles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var tmpl in source.PathTemplates)
                {
                    var values = UrlTemplate.Values(request.NormalizedFamily, style, null, repo);
                    string path = UrlTemplate.Expand(tmpl, values);
                    string url = $"{source.RawBaseUrl.TrimEnd('/')}/{repo.Owner}/{repo.Repo}/{repo.Branch}/{path.TrimStart('/')}";

                    var probe = await _http.ProbeAsync(url, null, probeTimeout, cancellationToken).ConfigureAwait(false);
                    if (!probe.Exists) continue;

                    byte[]? bytes = await _http.DownloadBytesAsync(url, null, MaxTtfBytes, downloadTimeout, cancellationToken).ConfigureAwait(false);
                    if (bytes == null) { Debug(context, $"vendor: download failed {url}"); continue; }

                    var license = LicenseEvaluator.Evaluate(source.LicenseHint, null, trustedSource: true);
                    var outcome = await FinalizeTtfAsync(request, source.Id, url, bytes, license, 0.9, style, context, cancellationToken).ConfigureAwait(false);
                    if (outcome.IsResolved && (primary == null || string.Equals(style, requestedToken, StringComparison.OrdinalIgnoreCase)))
                        primary = outcome;
                    break; // got this style
                }
            }

            return primary ?? FontAcquisitionResult.Miss(request, Layer, "no raw .ttf path matched");
        }

        private async Task<FontAcquisitionResult> TryReleaseArchiveAsync(
            FontSourceDefinition source, FontRequest request, ResolverContext context,
            TimeSpan downloadTimeout, CancellationToken cancellationToken)
        {
            var repo = source.Repo!;
            string apiUrl = string.IsNullOrWhiteSpace(repo.Tag)
                ? $"{source.ApiBaseUrl.TrimEnd('/')}/repos/{repo.Owner}/{repo.Repo}/releases/latest"
                : $"{source.ApiBaseUrl.TrimEnd('/')}/repos/{repo.Owner}/{repo.Repo}/releases/tags/{Uri.EscapeDataString(repo.Tag)}";

            var headers = new Dictionary<string, string>
            {
                ["Accept"] = "application/vnd.github+json",
                ["User-Agent"] = "MSPub2PDF/1.0 (+font-acquisition)",
            };

            string? json = await _http.GetStringAsync(apiUrl, headers, TimeSpan.FromMilliseconds(_config.Policy.DownloadTimeoutMs), cancellationToken).ConfigureAwait(false);
            if (json == null) { Debug(context, $"vendor: release metadata fetch failed for {source.Id}"); return FontAcquisitionResult.Miss(request, Layer, "release metadata unavailable"); }

            string? assetUrl = SelectAsset(json, repo.ReleaseAssetPattern!);
            if (assetUrl == null) { Debug(context, $"vendor: no asset matched '{repo.ReleaseAssetPattern}'"); return FontAcquisitionResult.Miss(request, Layer, "no matching release asset"); }

            byte[]? archive = await _http.DownloadBytesAsync(assetUrl, headers, MaxArchiveBytes, downloadTimeout, cancellationToken).ConfigureAwait(false);
            if (archive == null) { Debug(context, $"vendor: asset download failed {assetUrl}"); return FontAcquisitionResult.Miss(request, Layer, "asset download failed"); }

            ArchiveInspection inspection;
            try
            {
                using var ms = new MemoryStream(archive, writable: false);
                inspection = await _archiveInspector.InspectAsync(ms, source.Archive, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug(context, $"vendor: archive inspection failed — {ex.Message}");
                return FontAcquisitionResult.Miss(request, Layer, "archive inspection failed");
            }

            var matches = SelectArchiveTtfs(inspection, source, request.NormalizedFamily);
            if (matches.Count == 0) { Debug(context, "vendor: archive contained no matching .ttf"); return FontAcquisitionResult.Miss(request, Layer, "no matching .ttf in archive"); }

            var license = LicenseEvaluator.Evaluate(source.LicenseHint, inspection.LicenseText, trustedSource: true);
            FontAcquisitionResult? primary = null;
            string requestedToken = FontFamilyNormalizer.StyleFileToken(request.RequestedStyles);
            foreach (var font in matches)
            {
                var outcome = await FinalizeTtfAsync(request, source.Id, assetUrl, font.Data, license, 0.85, StyleOf(font.EntryName, requestedToken), context, cancellationToken).ConfigureAwait(false);
                if (outcome.IsResolved && primary == null) primary = outcome;
            }
            return primary ?? FontAcquisitionResult.Miss(request, Layer, "archive .ttf install failed");
        }

        // ---- helpers (exposed for tests via InternalsVisibleTo) ----

        internal static string? SelectAsset(string releaseJson, string pattern)
        {
            try
            {
                using var doc = JsonDocument.Parse(releaseJson);
                if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
                var rx = GlobToRegex(pattern);
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                    if (rx.IsMatch(name) && a.TryGetProperty("browser_download_url", out var u))
                        return u.GetString();
                }
            }
            catch (JsonException)
            {
            }
            return null;
        }

        internal List<ArchivedFont> SelectArchiveTtfs(ArchiveInspection inspection, FontSourceDefinition source, string family)
        {
            var hints = source.Archive?.TtfPathHints ?? new List<string>();
            string slug = UrlTemplate.Slug(family);
            var result = new List<ArchivedFont>();

            foreach (var font in inspection.Fonts)
            {
                // Prefer a real family-name match; fall back to path-hint or slug match.
                bool familyMatch = FontFileInspector.GetFamilyNames(font.Data)
                    .Any(f => FontNameNormalizer.Normalize(f) == FontNameNormalizer.Normalize(family));
                bool hintMatch = hints.Count > 0 && hints.Any(h => font.EntryName.Replace('\\', '/').Contains(h, StringComparison.OrdinalIgnoreCase));
                bool slugMatch = UrlTemplate.Slug(Path.GetFileNameWithoutExtension(font.EntryName)).Contains(slug, StringComparison.Ordinal);

                if (familyMatch || (hintMatch && slugMatch) || (hints.Count == 0 && slugMatch))
                    result.Add(font);
            }
            return result;
        }

        internal static Regex GlobToRegex(string glob)
        {
            var sb = new System.Text.StringBuilder("^");
            foreach (char c in glob)
            {
                if (c == '*') sb.Append(".*");
                else if (c == '?') sb.Append('.');
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
        }

        private static string StyleOf(string entryName, string fallback)
        {
            string name = Path.GetFileNameWithoutExtension(entryName);
            foreach (var token in new[] { "BoldItalic", "BoldOblique", "Bold", "Italic", "Oblique", "Regular", "Medium", "SemiBold", "Light", "Thin", "Black", "ExtraBold", "ExtraLight" })
            {
                if (name.EndsWith("-" + token, StringComparison.OrdinalIgnoreCase) || name.EndsWith(token, StringComparison.OrdinalIgnoreCase))
                    return token;
            }
            return fallback;
        }
    }
}

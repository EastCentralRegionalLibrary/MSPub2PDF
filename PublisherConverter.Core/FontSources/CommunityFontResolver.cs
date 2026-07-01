using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FeaturesOnDemand;
using PublisherConverter.Core.FontWorker;

namespace PublisherConverter.Core.FontSources
{
    /// <summary>
    /// Layer 4 — last-resort community aggregator (DaFont). Tries a direct slug
    /// download first, then a search-page lookup whose best match must clear a
    /// confidence threshold before anything is fetched. Archives are inspected in
    /// memory, only .ttf payloads are kept, and the license gate runs <em>before</em>
    /// install — an unclear license yields ManualReviewRequired rather than an
    /// automatic install.
    /// </summary>
    public sealed class CommunityFontResolver : FontSourceResolverBase
    {
        private const long MaxArchiveBytes = 100 * 1024 * 1024;

        // Smallest possible ZIP is a 22-byte End-Of-Central-Directory record.
        // Anything shorter is an empty/error response (DaFont answers a missing
        // font with HTTP 200 + a zero-length body), never a real archive. The
        // canonical value lives on FontArchiveInspector; alias it here so the
        // literal isn't duplicated across resolvers.
        private const int MinArchiveBytes = FontArchiveInspector.MinArchiveBytes;

        private const double DefaultMatchThreshold = 0.6;

        private static readonly Regex SlugRegex = new Regex(@"[?&]f=([a-zA-Z0-9_]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly FontSourceConfiguration _config;
        private readonly IFontHttpClient _http;
        private readonly FontArchiveInspector _archiveInspector;
        private readonly DisambiguationCallback? _disambiguationCallback;

        public CommunityFontResolver(
            FontSourceConfiguration config, IFontHttpClient http,
            FontArchiveInspector archiveInspector, FontLicenseEvaluator license, IStructuredLogger? logger = null,
            DisambiguationCallback? disambiguationCallback = null)
            : base(license, logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _archiveInspector = archiveInspector ?? throw new ArgumentNullException(nameof(archiveInspector));
            _disambiguationCallback = disambiguationCallback;
        }

        public override ResolutionLayer Layer => ResolutionLayer.Community;

        public override bool IsEnabled =>
            _config.IsLayerEnabled(ResolutionLayer.Community)
            && _config.Policy.CommunityEnabled
            && _config.Enabled(FontSourceType.Community).Any();

        public override async Task<FontAcquisitionResult> TryResolveAsync(FontRequest request, ResolverContext context, CancellationToken cancellationToken)
        {
            TimeSpan probeTimeout = TimeSpan.FromMilliseconds(_config.Policy.ProbeTimeoutMs);
            TimeSpan downloadTimeout = TimeSpan.FromMilliseconds(_config.Policy.DownloadTimeoutMs);

            foreach (var source in _config.Enabled(FontSourceType.Community))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (archive, sourceUrl, confidence) = await AcquireArchiveAsync(source, request, context, probeTimeout, downloadTimeout, cancellationToken).ConfigureAwait(false);
                if (archive == null) continue;

                var result = await InstallFromArchiveAsync(source, request, context, archive, sourceUrl!, confidence, cancellationToken).ConfigureAwait(false);
                if (result.Status != AcquisitionStatus.Missing) return result; // resolved, manual-review, or rejected
            }

            return FontAcquisitionResult.Miss(request, Layer, "no community match");
        }

        private async Task<(byte[]? archive, string? url, double confidence)> AcquireArchiveAsync(
            FontSourceDefinition source, FontRequest request, ResolverContext context,
            TimeSpan probeTimeout, TimeSpan downloadTimeout, CancellationToken cancellationToken)
        {
            string slug = UrlTemplate.Slug(request.NormalizedFamily);
            var headers = Headers(source);

            // Step A — direct slug guess.
            if (source.ProbeStrategy != ProbeStrategy.SearchOnly && !string.IsNullOrWhiteSpace(source.SlugTemplate))
            {
                string url = UrlTemplate.Expand(source.SlugTemplate!, new Dictionary<string, string> { ["slug"] = slug, ["query"] = slug });
                var probe = await _http.ProbeAsync(url, headers, probeTimeout, cancellationToken).ConfigureAwait(false);
                if (probe.Exists)
                {
                    byte[]? bytes = await _http.DownloadBytesAsync(url, headers, MaxArchiveBytes, downloadTimeout, cancellationToken).ConfigureAwait(false);
                    if (bytes != null && bytes.Length >= MinArchiveBytes) return (bytes, url, 0.9);
                    if (bytes != null) Debug(context, $"community: empty/short response ({bytes.Length} bytes) for '{slug}' — treating as miss");
                }
                Debug(context, $"community: direct slug '{slug}' missed");
            }

            // Step B — search page fallback.
            if (source.ProbeStrategy != ProbeStrategy.SlugOnly && !string.IsNullOrWhiteSpace(source.SearchTemplate) && !string.IsNullOrWhiteSpace(source.SlugTemplate))
            {
                string searchUrl = UrlTemplate.Expand(source.SearchTemplate!, new Dictionary<string, string> { ["query"] = Uri.EscapeDataString(request.NormalizedFamily), ["slug"] = slug });
                string? html = await _http.GetStringAsync(searchUrl, headers, downloadTimeout, cancellationToken).ConfigureAwait(false);
                if (html != null)
                {
                    var matches = SearchMatches(html, slug);
                    if (matches.Count == 0)
                    {
                        Debug(context, $"community: no search match above threshold for '{slug}'");
                    }
                    else
                    {
                        var chosen = await ChooseSearchMatchAsync(source, request, matches, context, cancellationToken).ConfigureAwait(false);
                        if (chosen != null)
                        {
                            string url = UrlTemplate.Expand(source.SlugTemplate!, new Dictionary<string, string> { ["slug"] = chosen.Value.slug, ["query"] = chosen.Value.slug });
                            byte[]? bytes = await _http.DownloadBytesAsync(url, headers, MaxArchiveBytes, downloadTimeout, cancellationToken).ConfigureAwait(false);
                            if (bytes != null && bytes.Length >= MinArchiveBytes) return (bytes, url, chosen.Value.confidence);
                            if (bytes != null) Debug(context, $"community: empty/short response ({bytes.Length} bytes) for '{chosen.Value.slug}' — treating as miss");
                        }
                    }
                }
            }

            return (null, null, 0);
        }

        // Picks among above-threshold search matches: a single match (or no callback)
        // takes the highest confidence; otherwise the user disambiguates. Index -1
        // (or an out-of-range / throwing callback) cancels this source.
        private async Task<(string slug, double confidence)?> ChooseSearchMatchAsync(
            FontSourceDefinition source, FontRequest request,
            List<(string slug, double confidence)> matches, ResolverContext context, CancellationToken cancellationToken)
        {
            if (matches.Count == 1 || _disambiguationCallback == null) return matches[0];

            var candidates = matches
                .Select(m => new DisambiguationCandidate { Slug = m.slug, SourceId = source.Id, Confidence = m.confidence })
                .ToList();

            int index;
            try
            {
                index = await _disambiguationCallback(request.RequestedName, candidates, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug(context, $"community: disambiguation callback threw — {ex.Message}; skipping source");
                return null;
            }

            if (index < 0 || index >= matches.Count)
            {
                Debug(context, $"community: user cancelled disambiguation for '{request.RequestedName}'");
                return null;
            }
            return matches[index];
        }

        private static IDictionary<string, string>? Headers(FontSourceDefinition source)
            => source.DefaultRequestHeaders.Count > 0 ? source.DefaultRequestHeaders : null;

        private async Task<FontAcquisitionResult> InstallFromArchiveAsync(
            FontSourceDefinition source, FontRequest request, ResolverContext context,
            byte[] archive, string sourceUrl, double confidence, CancellationToken cancellationToken)
        {
            ArchiveInspection inspection;
            try
            {
                using var ms = new MemoryStream(archive, writable: false);
                inspection = await _archiveInspector.InspectAsync(ms, source.Archive, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug(context, $"community: archive inspection failed — {ex.Message}");
                return FontAcquisitionResult.Miss(request, Layer, "archive inspection failed");
            }

            if (inspection.Fonts.Count == 0)
            {
                Debug(context, "community: archive had no .ttf payloads");
                return FontAcquisitionResult.Miss(request, Layer, "no .ttf in archive");
            }

            // License gate runs once for the archive, before any install.
            var license = LicenseEvaluator.Evaluate(source.LicenseHint, inspection.LicenseText, trustedSource: false);

            var chosen = ChooseFonts(inspection, request.NormalizedFamily);
            FontAcquisitionResult? primary = null;
            foreach (var font in chosen)
            {
                var outcome = await FinalizeTtfAsync(request, source.Id, sourceUrl, font.Data, license, confidence, "Regular", context, cancellationToken, inspection.LicenseText).ConfigureAwait(false);
                // Carry the first non-missing outcome (installed / manual-review / rejected).
                if (primary == null && outcome.Status != AcquisitionStatus.Missing) primary = outcome;
                if (outcome.IsResolved) { primary = outcome; break; }
            }
            return primary ?? FontAcquisitionResult.Miss(request, Layer, "no usable .ttf in archive");
        }

        private static List<ArchivedFont> ChooseFonts(ArchiveInspection inspection, string family)
        {
            string slug = UrlTemplate.Slug(family);
            var familyMatches = inspection.Fonts.Where(f =>
                FontFileInspector.GetFamilyNames(f.Data).Any(n => FontNameNormalizer.Normalize(n) == FontNameNormalizer.Normalize(family))
                || UrlTemplate.Slug(Path.GetFileNameWithoutExtension(f.EntryName)).Contains(slug, StringComparison.Ordinal)).ToList();

            if (familyMatches.Count > 0) return familyMatches;
            // A single-font archive is almost certainly the requested family.
            return inspection.Fonts.Count == 1 ? new List<ArchivedFont>(inspection.Fonts) : new List<ArchivedFont>();
        }

        // Returns every distinct candidate slug scoring at/above the threshold,
        // ordered by descending confidence — so a single hit proceeds directly and
        // multiple hits can be disambiguated by the user instead of silently
        // taking the top one.
        internal static List<(string slug, double confidence)> SearchMatches(string html, string targetSlug, double threshold = DefaultMatchThreshold)
        {
            var scored = new List<(string slug, double confidence)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in SlugRegex.Matches(html))
            {
                string candidate = m.Groups[1].Value.ToLowerInvariant();
                if (!seen.Add(candidate)) continue;
                double score = SlugSimilarity(targetSlug, candidate);
                if (score >= threshold) scored.Add((candidate, score));
            }
            scored.Sort((a, b) => b.confidence.CompareTo(a.confidence));
            return scored;
        }

        internal static double SlugSimilarity(string a, string b)
        {
            if (a == b) return 1.0;
            if (a.Length == 0 || b.Length == 0) return 0;
            if (b.StartsWith(a, StringComparison.Ordinal) || a.StartsWith(b, StringComparison.Ordinal)) return 0.85;
            if (b.Contains(a, StringComparison.Ordinal) || a.Contains(b, StringComparison.Ordinal)) return 0.7;

            int distance = Levenshtein(a, b);
            int max = Math.Max(a.Length, b.Length);
            return Math.Max(0, 1.0 - (double)distance / max);
        }

        private static int Levenshtein(string a, string b)
        {
            var prev = new int[b.Length + 1];
            var curr = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
                }
                (prev, curr) = (curr, prev);
            }
            return prev[b.Length];
        }
    }
}

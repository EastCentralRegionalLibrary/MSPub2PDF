using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FontWorker;

namespace PublisherConverter.Core.FontSources
{
    /// <summary>
    /// Layer 2 — resolves open-source families from the repository-backed
    /// <c>google/fonts</c> index instead of scraping the CSS endpoint, so only
    /// authoritative <c>.ttf</c> assets are acquired. The family's license
    /// sub-directory (ofl/apache/ufl) and the file-name layout come from config;
    /// a fast HEAD probe locates static files before anything is downloaded.
    /// When no static file matches (many families — Roboto, Open Sans — now ship
    /// only variable fonts), the family's METADATA.pb is consulted for the
    /// authoritative file list. A declared static file installs directly; a
    /// variable-only family is offered to the user as a clearly labeled
    /// variable-font substitute via the disambiguation callback and installs only
    /// on acceptance (no callback wired → skipped, never installed unprompted).
    /// The accept/decline decision is remembered per family for the acquisition
    /// operation.
    /// </summary>
    public sealed class GoogleFontsResolver : FontSourceResolverBase
    {
        private readonly FontSourceConfiguration _config;
        private readonly IFontHttpClient _http;
        private readonly DisambiguationCallback? _variableFontCallback;

        // Variable-font decisions already taken this acquisition operation,
        // keyed by normalized family. The scratch directory is unique per cycle,
        // so it scopes "remember for the operation" without a new mechanism.
        private readonly object _vfDecisionLock = new object();
        private string? _vfDecisionScope;
        private readonly Dictionary<string, bool> _vfDecisions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public GoogleFontsResolver(
            FontSourceConfiguration config, IFontHttpClient http, FontLicenseEvaluator license,
            IStructuredLogger? logger = null, DisambiguationCallback? variableFontCallback = null)
            : base(license, logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _variableFontCallback = variableFontCallback;
        }

        public override ResolutionLayer Layer => ResolutionLayer.GoogleFonts;

        public override bool IsEnabled => _config.IsLayerEnabled(ResolutionLayer.GoogleFonts) && _config.Enabled(FontSourceType.GoogleFonts).Any();

        public override async Task<FontAcquisitionResult> TryResolveAsync(FontRequest request, ResolverContext context, CancellationToken cancellationToken)
        {
            TimeSpan probeTimeout = TimeSpan.FromMilliseconds(_config.Policy.ProbeTimeoutMs);
            TimeSpan downloadTimeout = TimeSpan.FromMilliseconds(_config.Policy.DownloadTimeoutMs);

            // Keep the most specific miss (e.g. "variable-font substitute not
            // accepted") instead of flattening it into the generic reason.
            FontAcquisitionResult? lastAttempt = null;
            foreach (var source in _config.Enabled(FontSourceType.GoogleFonts))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await TryFromSourceAsync(source, request, context, probeTimeout, downloadTimeout, cancellationToken).ConfigureAwait(false);
                if (result.IsResolved) return result;
                lastAttempt = result;
            }
            return lastAttempt ?? FontAcquisitionResult.Miss(request, Layer, "no Google Fonts match");
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
                    // No static Regular anywhere — the family may still exist as a
                    // variable-only family, which METADATA.pb settles authoritatively.
                    if (style == "Regular" && licenseDir == null)
                        return await TryFromMetadataAsync(source, request, context, downloadTimeout, cancellationToken).ConfigureAwait(false);
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

        // ---- METADATA.pb fallback (variable-only and nonstandard families) ----

        private async Task<FontAcquisitionResult> TryFromMetadataAsync(
            FontSourceDefinition source, FontRequest request, ResolverContext context,
            TimeSpan downloadTimeout, CancellationToken cancellationToken)
        {
            var repo = source.Repo!;
            string? metadata = null;
            string? licenseDir = null;

            foreach (var dir in source.LicenseDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = UrlTemplate.Values(request.NormalizedFamily, "Regular", dir, repo);
                string path = UrlTemplate.Expand(source.MetadataPathTemplate, values);
                string url = $"{source.RawBaseUrl.TrimEnd('/')}/{repo.Owner}/{repo.Repo}/{repo.Branch}/{path.TrimStart('/')}";

                metadata = await _http.GetStringAsync(url, null, downloadTimeout, cancellationToken).ConfigureAwait(false);
                if (metadata != null) { licenseDir = dir; break; }
            }
            if (metadata == null || licenseDir == null)
                return FontAcquisitionResult.Miss(request, Layer, "family not found in google/fonts");

            var declared = ParseMetadataFilenames(metadata)
                .Where(f => source.SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();
            if (declared.Count == 0)
                return FontAcquisitionResult.Miss(request, Layer, "METADATA.pb declared no installable files");

            var staticFiles = declared.Where(f => !f.Contains('[')).ToList();
            var variableFiles = declared.Where(f => f.Contains('[')).ToList();

            // Declared static files install directly — no prompt, same trust as
            // the template fast path.
            if (staticFiles.Count > 0)
                return await InstallDeclaredFilesAsync(source, request, context, licenseDir, staticFiles, downloadTimeout, cancellationToken).ConfigureAwait(false);

            // Variable-only family: the user must opt in to the VF substitute.
            bool accepted = await ConfirmVariableFontAsync(source, request, context, cancellationToken).ConfigureAwait(false);
            if (!accepted)
                return FontAcquisitionResult.Miss(request, Layer, "variable-font substitute not accepted — skipping google/fonts");

            return await InstallDeclaredFilesAsync(source, request, context, licenseDir, variableFiles, downloadTimeout, cancellationToken).ConfigureAwait(false);
        }

        private async Task<FontAcquisitionResult> InstallDeclaredFilesAsync(
            FontSourceDefinition source, FontRequest request, ResolverContext context,
            string licenseDir, IReadOnlyList<string> filenames,
            TimeSpan downloadTimeout, CancellationToken cancellationToken)
        {
            var repo = source.Repo!;
            string requestedToken = FontFamilyNormalizer.StyleFileToken(request.RequestedStyles);
            string slug = UrlTemplate.Slug(request.NormalizedFamily);
            FontAcquisitionResult? primary = null;

            foreach (var filename in filenames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string url = $"{source.RawBaseUrl.TrimEnd('/')}/{repo.Owner}/{repo.Repo}/{repo.Branch}/{licenseDir}/{slug}/{Uri.EscapeDataString(filename)}";

                byte[]? bytes = await _http.DownloadBytesAsync(url, null, MaxTtfBytes, downloadTimeout, cancellationToken).ConfigureAwait(false);
                if (bytes == null)
                {
                    Debug(context, $"google: download failed for {url}");
                    continue;
                }

                string style = DeclaredStyleToken(filename);
                var license = LicenseEvaluator.Evaluate(source.LicenseHint, null, trustedSource: true);
                var outcome = await FinalizeTtfAsync(request, source.Id, url, bytes, license, 0.95, style, context, cancellationToken).ConfigureAwait(false);

                if (outcome.IsResolved && (primary == null || string.Equals(style, requestedToken, StringComparison.OrdinalIgnoreCase)))
                {
                    primary = outcome;
                }
            }

            return primary ?? FontAcquisitionResult.Miss(request, Layer, "no installable .ttf from METADATA.pb file list");
        }

        // Surfaces the variable-font substitute through the existing
        // disambiguation prompt: one clearly labeled candidate, any chosen index
        // accepts, cancel (-1) declines. No callback wired → skip, never install
        // a VF unprompted. The answer is remembered per family for the operation.
        private async Task<bool> ConfirmVariableFontAsync(
            FontSourceDefinition source, FontRequest request, ResolverContext context, CancellationToken cancellationToken)
        {
            string family = request.NormalizedFamily;
            lock (_vfDecisionLock)
            {
                if (!string.Equals(_vfDecisionScope, context.ScratchDirectory, StringComparison.Ordinal))
                {
                    _vfDecisionScope = context.ScratchDirectory;
                    _vfDecisions.Clear();
                }
                if (_vfDecisions.TryGetValue(family, out bool remembered)) return remembered;
            }

            bool accepted = false;
            if (_variableFontCallback == null)
            {
                Debug(context, $"google: '{family}' exists only as a variable font and no prompt callback is wired — skipping");
            }
            else
            {
                var candidates = new List<DisambiguationCandidate>
                {
                    new DisambiguationCandidate
                    {
                        Slug = $"{family} — variable font",
                        SourceId = source.Id,
                        Confidence = 0.95,
                    },
                };

                try
                {
                    int index = await _variableFontCallback(request.RequestedName, candidates, cancellationToken).ConfigureAwait(false);
                    accepted = index >= 0 && index < candidates.Count;
                    if (!accepted) Debug(context, $"google: user declined variable-font substitute for '{family}'");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug(context, $"google: variable-font prompt threw — {ex.Message}; skipping");
                }
            }

            lock (_vfDecisionLock)
            {
                _vfDecisions[family] = accepted;
            }
            return accepted;
        }

        /// <summary>
        /// Extracts the <c>filename:</c> entries from a google/fonts METADATA.pb
        /// (text-format protobuf) — the authoritative list of a family's font
        /// files, correct for both static and variable layouts.
        /// </summary>
        internal static IReadOnlyList<string> ParseMetadataFilenames(string metadata)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(metadata)) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in MetadataFilenameRegex.Matches(metadata))
            {
                string filename = m.Groups[1].Value.Trim();
                if (filename.Length > 0 && seen.Add(filename)) result.Add(filename);
            }
            return result;
        }

        // Style token carried by a declared filename: the suffix after the last
        // '-', with any variable-axis part ("[wdth,wght]") stripped first, so
        // "Roboto[wdth,wght].ttf" → Regular and "Roboto-Italic[wdth,wght].ttf" → Italic.
        internal static string DeclaredStyleToken(string filename)
        {
            string name = Path.GetFileNameWithoutExtension(filename);
            int bracket = name.IndexOf('[');
            if (bracket >= 0) name = name.Substring(0, bracket);
            int dash = name.LastIndexOf('-');
            return dash >= 0 && dash < name.Length - 1 ? name.Substring(dash + 1) : "Regular";
        }

        private static readonly Regex MetadataFilenameRegex =
            new Regex("^\\s*filename:\\s*\"([^\"]+)\"", RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

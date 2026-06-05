using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Resolves fonts referenced in the <c>googleFonts</c> mapping section
    /// using the public, unauthenticated Google Fonts CSS endpoint at
    /// <c>https://fonts.googleapis.com/css?family=…</c>. A legacy User-Agent
    /// is sent so the response includes TTF URLs (modern UAs receive WOFF2,
    /// which Windows can't HKCU-register). Each <c>src: url(...)</c> in the
    /// response is downloaded and installed via
    /// <see cref="IUserFontInstaller"/>; non-TTF/OTF entries are logged and
    /// skipped so the resolver falls through to the next strategy
    /// (typically GitHub) for that font.
    ///
    /// No API key, no developer-API call — purely the public CDN.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class GoogleFontsProvisioningStrategy : IFontProvisioningStrategy
    {
        // Old (v1) CSS endpoint: returns TTF URLs for legacy UAs more
        // reliably than the css2 endpoint, which is WOFF2-only.
        private const string CssEndpoint = "https://fonts.googleapis.com/css?family={0}";

        // IE9/IE10-class UA — Google Fonts' v1 CSS endpoint serves TTF for
        // these. Modern Chrome/Firefox UAs get WOFF2 only.
        private const string LegacyUserAgent =
            "Mozilla/5.0 (compatible; MSIE 9.0; Windows NT 6.1; Trident/5.0)";

        private static readonly Regex SrcUrlRegex = new Regex(
            @"url\(\s*['""]?([^'""\)]+)['""]?\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly FontMappingTable _mappings;
        private readonly IFontDownloader _downloader;
        private readonly IUserFontInstaller _installer;

        public GoogleFontsProvisioningStrategy(
            FontMappingTable mappings,
            IFontDownloader downloader,
            IUserFontInstaller installer)
        {
            _mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        }

        public string Name => "GoogleFonts";

        public bool CanResolve(string fontFamily) => _mappings.TryGetGoogleFont(fontFamily, out _);

        public async Task<bool> TryResolveAsync(string fontFamily, IList<string> log, CancellationToken cancellationToken)
        {
            if (!_mappings.TryGetGoogleFont(fontFamily, out var spec)) return false;

            string queryFamily = !string.IsNullOrWhiteSpace(spec.Family) ? spec.Family! : fontFamily;
            string installFamily = !string.IsNullOrWhiteSpace(spec.Family) ? spec.Family! : fontFamily;

            string cssUrl = string.Format(CssEndpoint, Uri.EscapeDataString(queryFamily));
            log.Add($"    GET {cssUrl}");

            string css;
            try
            {
                var headers = new Dictionary<string, string> { ["User-Agent"] = LegacyUserAgent };
                css = await _downloader.DownloadStringAsync(cssUrl, headers, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.Add($"    ! Google Fonts CSS fetch failed: {ex.Message}");
                return false;
            }

            var fontUrls = ExtractSrcUrls(css);
            if (fontUrls.Count == 0)
            {
                log.Add($"    ! CSS response contained no font URLs; family may not exist on Google Fonts.");
                return false;
            }

            log.Add($"    CSS response yielded {fontUrls.Count} variant URL(s).");
            bool any = false;
            foreach (var url in fontUrls)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string ext = ExtractFileExtension(url);
                if (!IsInstallableFontExtension(ext))
                {
                    log.Add($"    × skipping non-installable format ({(string.IsNullOrEmpty(ext) ? "no extension" : ext)}): {url}");
                    continue;
                }

                string suggestedName = DeriveFileName(url, queryFamily, ext);
                if (await _installer.InstallFromUrlAsync(installFamily, url, suggestedName, log, cancellationToken).ConfigureAwait(false))
                {
                    any = true;
                }
            }

            if (!any)
            {
                log.Add("    no TTF/OTF variants installable from the Google Fonts response — consider a GitHub mapping for this font.");
            }
            return any;
        }

        // ---- internals exposed for unit tests via InternalsVisibleTo ----

        internal static List<string> ExtractSrcUrls(string? css)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(css)) return result;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in SrcUrlRegex.Matches(css))
            {
                string url = m.Groups[1].Value.Trim();
                if (url.Length == 0) continue;
                if (seen.Add(url)) result.Add(url);
            }
            return result;
        }

        internal static string ExtractFileExtension(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            try
            {
                var uri = new Uri(url, UriKind.RelativeOrAbsolute);
                string path = uri.IsAbsoluteUri ? uri.AbsolutePath : url;
                return Path.GetExtension(path).ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static bool IsInstallableFontExtension(string extension)
            => extension == ".ttf" || extension == ".otf";

        internal static string DeriveFileName(string url, string family, string ext)
        {
            try
            {
                var uri = new Uri(url, UriKind.RelativeOrAbsolute);
                if (uri.IsAbsoluteUri)
                {
                    string name = Path.GetFileName(uri.LocalPath);
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
            catch { }
            string safeFamily = string.IsNullOrWhiteSpace(family) ? "Font" : family.Replace(" ", "");
            return $"{safeFamily}-Regular{(string.IsNullOrEmpty(ext) ? ".ttf" : ext)}";
        }
    }
}

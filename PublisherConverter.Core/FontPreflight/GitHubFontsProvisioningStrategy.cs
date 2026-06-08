using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Resolves fonts referenced in the <c>githubFonts</c> mapping section
    /// from public, unauthenticated GitHub repositories. Two modes selected
    /// per-spec:
    ///   * Raw-content mode (<see cref="GitHubFontSpec.Path"/> set) —
    ///     downloads from <c>raw.githubusercontent.com</c> at the given
    ///     ref/branch (defaults to <c>main</c>).
    ///   * Release-asset mode (Path null) — queries the GitHub releases
    ///     REST API for the named release (default <c>latest</c>), filters
    ///     assets by exact name or glob, and downloads each match.
    ///
    /// Either mode handles archives transparently: when the resolved
    /// payload is a <c>.zip</c>, every <c>.ttf</c> / <c>.otf</c> entry
    /// inside is installed via <see cref="IUserFontInstaller"/>.
    ///
    /// Unauthenticated calls — no token, no API key. Subject to GitHub's
    /// 60-req/hour anon rate limit; transient 4xx/5xx are reported via the
    /// log and return false so the resolver continues with other strategies.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class GitHubFontsProvisioningStrategy : IFontProvisioningStrategy
    {
        private const string ApiBase = "https://api.github.com";
        private const string RawBase = "https://raw.githubusercontent.com";
        private const string DefaultRef = "main";
        private const string DefaultRelease = "latest";

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly FontMappingTable _mappings;
        private readonly IFontDownloader _downloader;
        private readonly IUserFontInstaller _installer;

        public GitHubFontsProvisioningStrategy(
            FontMappingTable mappings,
            IFontDownloader downloader,
            IUserFontInstaller installer)
        {
            _mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        }

        public string Name => "GitHubFonts";

        public bool CanResolve(string fontFamily) => _mappings.TryGetGitHubFont(fontFamily, out _);

        public async Task<bool> TryResolveAsync(string fontFamily, IList<string> log, CancellationToken cancellationToken)
        {
            if (!_mappings.TryGetGitHubFont(fontFamily, out var spec)) return false;
            if (string.IsNullOrWhiteSpace(spec.Owner) || string.IsNullOrWhiteSpace(spec.Repo))
            {
                log.Add("    ! GitHub spec is missing owner or repo.");
                return false;
            }

            string installFamily = !string.IsNullOrWhiteSpace(spec.Family) ? spec.Family! : fontFamily;

            // Raw-content mode wins when Path is set.
            if (!string.IsNullOrWhiteSpace(spec.Path))
            {
                string @ref = !string.IsNullOrWhiteSpace(spec.Ref) ? spec.Ref! : DefaultRef;
                string url = $"{RawBase}/{spec.Owner}/{spec.Repo}/{@ref}/{Trim(spec.Path!)}";
                string filename = System.IO.Path.GetFileName(spec.Path!);
                log.Add($"    raw-content: {url}");
                return await InstallByUrlAsync(url, filename, installFamily, log, cancellationToken).ConfigureAwait(false);
            }

            // Release-asset mode.
            string release = !string.IsNullOrWhiteSpace(spec.Release) ? spec.Release! : DefaultRelease;
            log.Add($"    release: {spec.Owner}/{spec.Repo}@{release}");

            ReleaseAsset[] assets;
            try
            {
                assets = await FetchReleaseAssetsAsync(spec.Owner, spec.Repo, release, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.Add($"    ! release fetch failed: {ex.Message}");
                return false;
            }

            if (assets.Length == 0)
            {
                log.Add("    ! release contained no assets.");
                return false;
            }

            var matched = FilterAssets(assets, spec.Asset, spec.AssetPattern);
            if (matched.Count == 0)
            {
                log.Add($"    ! no assets matched (asset='{spec.Asset}', pattern='{spec.AssetPattern}'). Available: {string.Join(", ", AssetNames(assets))}");
                return false;
            }

            bool any = false;
            foreach (var asset in matched)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await InstallByUrlAsync(asset.BrowserDownloadUrl, asset.Name, installFamily, log, cancellationToken).ConfigureAwait(false))
                {
                    any = true;
                }
            }
            return any;
        }

        private async Task<bool> InstallByUrlAsync(string url, string filename, string installFamily, IList<string> log, CancellationToken cancellationToken)
        {
            if (IsArchive(filename))
            {
                return await InstallFromArchiveAsync(url, filename, installFamily, log, cancellationToken).ConfigureAwait(false);
            }
            if (!IsFontFile(filename))
            {
                log.Add($"    × skipping non-font, non-archive payload: {filename}");
                return false;
            }
            return await _installer.InstallFromUrlAsync(installFamily, url, filename, log, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> InstallFromArchiveAsync(string url, string archiveName, string installFamily, IList<string> log, CancellationToken cancellationToken)
        {
            log.Add($"    downloading archive {archiveName}");
            using var ms = new MemoryStream();
            try
            {
                await _downloader.DownloadAsync(url, ms, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.Add($"    ! archive download failed: {ex.Message}");
                return false;
            }
            ms.Position = 0;

            bool any = false;
            int fontCount = 0;
            try
            {
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // directory entries
                    if (!IsFontFile(entry.Name)) continue;
                    fontCount++;
                    log.Add($"    extracting {entry.FullName}");
                    using var src = entry.Open();
                    if (await _installer.InstallFromStreamAsync(installFamily, entry.Name, src, log, cancellationToken).ConfigureAwait(false))
                    {
                        any = true;
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                log.Add($"    ! archive payload is not a valid ZIP: {ex.Message}");
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.Add($"    ! archive extraction failed: {ex.Message}");
                return false;
            }

            if (fontCount == 0)
            {
                log.Add("    ! archive contained no .ttf/.otf entries.");
            }
            return any;
        }

        private async Task<ReleaseAsset[]> FetchReleaseAssetsAsync(string owner, string repo, string release, CancellationToken cancellationToken)
        {
            string url = string.Equals(release, DefaultRelease, StringComparison.OrdinalIgnoreCase)
                ? $"{ApiBase}/repos/{owner}/{repo}/releases/latest"
                : $"{ApiBase}/repos/{owner}/{repo}/releases/tags/{Uri.EscapeDataString(release)}";

            var headers = new Dictionary<string, string>
            {
                ["Accept"] = "application/vnd.github+json",
                ["User-Agent"] = "MSPub2PDF/1.0 (+font-resolver)",
            };

            string json = await _downloader.DownloadStringAsync(url, headers, cancellationToken).ConfigureAwait(false);
            var release_json = JsonSerializer.Deserialize<ReleaseJson>(json, JsonOpts);
            return release_json?.Assets ?? Array.Empty<ReleaseAsset>();
        }

        // ---- internals exposed for tests via InternalsVisibleTo ----

        internal sealed class ReleaseJson
        {
            [JsonPropertyName("assets")]
            public ReleaseAsset[] Assets { get; set; } = Array.Empty<ReleaseAsset>();
        }

        internal sealed class ReleaseAsset
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("browser_download_url")]
            public string BrowserDownloadUrl { get; set; } = string.Empty;
        }

        internal static List<ReleaseAsset> FilterAssets(ReleaseAsset[] assets, string? exactName, string? pattern)
        {
            if (!string.IsNullOrWhiteSpace(exactName))
            {
                var result = new List<ReleaseAsset>();
                foreach (var a in assets)
                {
                    if (string.Equals(a.Name, exactName, StringComparison.OrdinalIgnoreCase)) result.Add(a);
                }
                return result;
            }
            if (!string.IsNullOrWhiteSpace(pattern))
            {
                var rx = GlobToRegex(pattern);
                var result = new List<ReleaseAsset>();
                foreach (var a in assets)
                {
                    if (rx.IsMatch(a.Name)) result.Add(a);
                }
                return result;
            }
            // No explicit filter — accept fonts directly and archives that
            // typically contain fonts.
            var defaults = new List<ReleaseAsset>();
            foreach (var a in assets)
            {
                if (IsFontFile(a.Name) || IsArchive(a.Name)) defaults.Add(a);
            }
            return defaults;
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
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        internal static bool IsFontFile(string name)
        {
            string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
            return ext == ".ttf" || ext == ".otf";
        }

        internal static bool IsArchive(string name)
        {
            string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
            return ext == ".zip";
        }

        private static IEnumerable<string> AssetNames(ReleaseAsset[] assets)
        {
            foreach (var a in assets) yield return a.Name;
        }

        private static string Trim(string s) => s.TrimStart('/');
    }
}

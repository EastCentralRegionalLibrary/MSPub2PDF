using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FontWorker;

namespace PublisherConverter.Core.FeaturesOnDemand
{
    /// <summary>Raised when the UUP Dump API returns nothing usable.</summary>
    public sealed class UupDumpException : Exception
    {
        public UupDumpException(string message) : base(message) { }
        public UupDumpException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Default <see cref="IUupDumpClient"/>. HTTP is delegated to the shared
    /// <see cref="IFontDownloader"/> so the whole app keeps a single, reusable
    /// HttpClient and the client is trivially faked in tests. JSON is parsed with
    /// <see cref="JsonDocument"/> to tolerate the API's loosely-typed fields
    /// (e.g. <c>size</c> arriving as either a number or a string, and
    /// <c>builds</c> arriving as either an array or an object).
    /// </summary>
    public sealed class UupDumpClient : IUupDumpClient
    {
        public const string DefaultApiBase = "https://api.uupdump.net";

        private readonly IFontDownloader _downloader;
        private readonly IStructuredLogger _logger;
        private readonly string _apiBase;

        public UupDumpClient(IFontDownloader downloader, IStructuredLogger? logger = null, string? apiBase = null)
        {
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _logger = logger ?? NullStructuredLogger.Instance;
            _apiBase = string.IsNullOrWhiteSpace(apiBase) ? DefaultApiBase : apiBase!.TrimEnd('/');
        }

        private string ListIdUrl => $"{_apiBase}/listid.php";
        private string GetUrl => $"{_apiBase}/get.php";

        public async Task<string> FindLatestUpdateIdAsync(string buildSearch, string architecture, string? correlationId, CancellationToken cancellationToken)
        {
            string arch = (architecture ?? string.Empty).Trim();
            string url = BuildUrl(ListIdUrl, ("search", buildSearch ?? string.Empty), ("sortByDate", "1"));
            _logger.Info("fod.listid.request", correlationId, Fields(("url", url), ("arch", arch)));

            string body = await GetStringAsync(url, correlationId, cancellationToken).ConfigureAwait(false);

            using var doc = ParseJson(body, "listid.php");
            if (!doc.RootElement.TryGetProperty("response", out var response))
            {
                throw new UupDumpException("listid.php response had no 'response' object.");
            }
            if (!response.TryGetProperty("builds", out var builds))
            {
                throw new UupDumpException($"No builds returned for search '{buildSearch}'.");
            }

            UupBuild? selected = SelectBuild(builds, arch);
            if (selected == null || string.IsNullOrWhiteSpace(selected.Uuid))
            {
                throw new UupDumpException($"No {arch} build found for search '{buildSearch}'.");
            }

            _logger.Info("fod.listid.selected", correlationId, Fields(
                ("title", selected.Title ?? "<unknown>"),
                ("uuid", selected.Uuid),
                ("arch", selected.Arch)));
            return selected.Uuid!;
        }

        public async Task<IReadOnlyDictionary<string, UupFile>> GetFilesAsync(string updateId, string? correlationId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(updateId)) throw new UupDumpException("Update id was empty.");

            string url = BuildUrl(GetUrl, ("id", updateId));
            _logger.Info("fod.get.request", correlationId, Fields(("url", url)));

            string body = await GetStringAsync(url, correlationId, cancellationToken).ConfigureAwait(false);

            using var doc = ParseJson(body, "get.php");
            if (!doc.RootElement.TryGetProperty("response", out var response) ||
                !response.TryGetProperty("files", out var files) ||
                files.ValueKind != JsonValueKind.Object)
            {
                throw new UupDumpException("No files returned by get.php.");
            }

            var result = new Dictionary<string, UupFile>(StringComparer.Ordinal);
            foreach (var prop in files.EnumerateObject())
            {
                result[prop.Name] = ParseFile(prop.Name, prop.Value);
            }

            if (result.Count == 0) throw new UupDumpException("get.php returned an empty file manifest.");

            _logger.Info("fod.get.files", correlationId, Fields(("count", result.Count)));
            return result;
        }

        // ---- package selection (mirrors CabDownloader.py:select_font_package) ----

        /// <summary>
        /// Selects the font CAB for <paramref name="language"/> from a manifest:
        /// the largest valid <paramref name="architecture"/> package whose name
        /// contains <c>Microsoft-Windows-LanguageFeatures-Fonts-{language}-Package</c>.
        /// Returns false (no throw) when nothing matches so a single language can
        /// fail without aborting the batch.
        /// </summary>
        public static bool TrySelectFontPackage(
            IReadOnlyDictionary<string, UupFile> files,
            string language,
            string architecture,
            string updateId,
            out ResolvedFontPackage package)
        {
            package = null!;
            if (files == null || string.IsNullOrWhiteSpace(language)) return false;

            string arch = (architecture ?? "amd64").Trim();
            string pattern = $"microsoft-windows-languagefeatures-fonts-{language}-package".ToLowerInvariant();
            string archMarker = $"-package-{arch}".ToLowerInvariant();

            UupFile? best = null;
            foreach (var kv in files)
            {
                string lower = kv.Key.ToLowerInvariant();
                if (!lower.Contains(pattern, StringComparison.Ordinal)) continue;
                if (!lower.EndsWith(".cab", StringComparison.Ordinal)) continue;
                if (!lower.Contains(archMarker, StringComparison.Ordinal)) continue;

                if (best == null || kv.Value.Size > best.Size) best = kv.Value;
            }

            if (best == null) return false;

            string? downloadUrl = best.Url;
            if (string.IsNullOrWhiteSpace(downloadUrl)) return false; // no URL → cannot download

            package = new ResolvedFontPackage
            {
                Language = language,
                FileName = best.FileName,
                DownloadUrl = downloadUrl!,
                SizeBytes = best.Size,
                Sha256 = best.Sha256,
                Sha1 = best.Sha1,
                Architecture = arch,
                UpdateId = updateId,
            };
            return true;
        }

        // ---- internals (exposed to tests via InternalsVisibleTo) ----

        internal static UupBuild? SelectBuild(JsonElement builds, string targetArch)
        {
            IEnumerable<JsonElement> Enumerate()
            {
                if (builds.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in builds.EnumerateArray()) yield return e;
                }
                else if (builds.ValueKind == JsonValueKind.Object)
                {
                    // The API sometimes returns builds as an object keyed by index.
                    foreach (var p in builds.EnumerateObject()) yield return p.Value;
                }
            }

            foreach (var element in Enumerate())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                string? arch = GetString(element, "arch");
                if (!string.Equals(arch ?? string.Empty, targetArch, StringComparison.OrdinalIgnoreCase)) continue;

                return new UupBuild
                {
                    Uuid = GetString(element, "uuid"),
                    Title = GetString(element, "title"),
                    Build = GetString(element, "build"),
                    Arch = arch,
                };
            }
            return null;
        }

        internal static UupFile ParseFile(string fileName, JsonElement meta)
        {
            long size = 0;
            string? url = null, sha256 = null, sha1 = null;

            if (meta.ValueKind == JsonValueKind.Object)
            {
                size = GetLong(meta, "size");
                // Mirror extract_download_url's field-name fallbacks.
                url = FirstNonEmpty(
                    GetString(meta, "url"),
                    GetString(meta, "downloadUrl"),
                    GetString(meta, "downloadURL"),
                    GetString(meta, "link"));
                sha256 = GetString(meta, "sha256");
                sha1 = GetString(meta, "sha1");
            }

            return new UupFile
            {
                FileName = fileName,
                Size = size,
                Url = url,
                Sha256 = sha256,
                Sha1 = sha1,
            };
        }

        private async Task<string> GetStringAsync(string url, string? correlationId, CancellationToken cancellationToken)
        {
            try
            {
                return await _downloader.DownloadStringAsync(url, null, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("fod.http.failed", correlationId, Fields(("url", url), ("error", ex.Message)));
                throw new UupDumpException($"UUP Dump request failed: {ex.Message}", ex);
            }
        }

        private static JsonDocument ParseJson(string body, string endpoint)
        {
            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new UupDumpException($"{endpoint} returned invalid JSON: {ex.Message}", ex);
            }
        }

        private static string BuildUrl(string baseUrl, params (string Key, string Value)[] query)
        {
            var sb = new StringBuilder(baseUrl);
            char sep = '?';
            foreach (var (k, v) in query)
            {
                sb.Append(sep).Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
                sep = '&';
            }
            return sb.ToString();
        }

        private static string? GetString(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.ToString(),
                _ => null,
            };
        }

        private static long GetLong(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var v)) return 0;
            switch (v.ValueKind)
            {
                case JsonValueKind.Number:
                    return v.TryGetInt64(out long n) ? n : 0;
                case JsonValueKind.String:
                    return long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long s) ? s : 0;
                default:
                    return 0;
            }
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return null;
        }

        private static IReadOnlyDictionary<string, object?> Fields(params (string Key, object? Value)[] pairs)
        {
            var d = new Dictionary<string, object?>(pairs.Length);
            foreach (var (k, v) in pairs) d[k] = v;
            return d;
        }
    }
}

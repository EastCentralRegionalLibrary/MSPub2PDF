using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PublisherConverter.Core
{
    /// <summary>
    /// A downloadable fallback font: where to fetch it and the family name it
    /// installs under. The original (missing) font name is the dictionary key
    /// in <see cref="FontMappingFile.ExternalFallbacks"/>.
    /// </summary>
    public sealed class FontFallback
    {
        /// <summary>Display / family name of the substitute, e.g. "Noto Serif KR".</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Optional explicit output filename, e.g. "NotoSerifKR-Regular.ttf".</summary>
        public string? FileName { get; set; }

        /// <summary>Direct download URL for a .ttf/.otf file.</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>Optional source hint ("github", "google", ...). Informational.</summary>
        public string? Source { get; set; }
    }

    /// <summary>
    /// Per-font configuration for the Google Fonts strategy. The key in
    /// <see cref="FontMappingFile.GoogleFonts"/> is the family the document
    /// references (matched after normalization). <see cref="Family"/> is the
    /// family used to query Google Fonts when it differs from the key —
    /// otherwise the key is used as-is.
    /// </summary>
    public sealed class GoogleFontSpec
    {
        /// <summary>Optional override for the Google Fonts family to query.</summary>
        public string? Family { get; set; }
    }

    /// <summary>
    /// Per-font configuration for the GitHub strategy. Two operating modes
    /// based on which fields are populated:
    ///   * Raw-content mode — set <see cref="Path"/> (and optionally
    ///     <see cref="Ref"/>). The strategy fetches
    ///     https://raw.githubusercontent.com/{owner}/{repo}/{ref}/{path}.
    ///   * Release-asset mode — leave Path null and set <see cref="Release"/>
    ///     (defaults to "latest"). The strategy hits the GitHub releases
    ///     API, then filters assets by <see cref="Asset"/> (exact match) or
    ///     <see cref="AssetPattern"/> (glob like "*.ttf").
    /// Both modes support archive payloads (.zip) — every .ttf/.otf entry
    /// inside is installed.
    /// </summary>
    public sealed class GitHubFontSpec
    {
        public string Owner { get; set; } = string.Empty;
        public string Repo { get; set; } = string.Empty;

        // Raw-content mode
        public string? Path { get; set; }
        public string? Ref { get; set; }

        // Release-asset mode
        public string? Release { get; set; }
        public string? Asset { get; set; }
        public string? AssetPattern { get; set; }

        // Optional display family (defaults to the mapping key)
        public string? Family { get; set; }
    }

    /// <summary>Raw on-disk shape of FontMapping.json.</summary>
    public sealed class FontMappingFile
    {
        [JsonPropertyName("windowsCapabilities")]
        public Dictionary<string, string> WindowsCapabilities { get; set; } = new Dictionary<string, string>();

        [JsonPropertyName("googleFonts")]
        public Dictionary<string, GoogleFontSpec> GoogleFonts { get; set; } = new Dictionary<string, GoogleFontSpec>();

        [JsonPropertyName("githubFonts")]
        public Dictionary<string, GitHubFontSpec> GitHubFonts { get; set; } = new Dictionary<string, GitHubFontSpec>();

        [JsonPropertyName("externalFallbacks")]
        public Dictionary<string, FontFallback> ExternalFallbacks { get; set; } = new Dictionary<string, FontFallback>();
    }

    /// <summary>
    /// Normalized, queryable view of the font mapping. Keys are matched with
    /// <see cref="FontNameNormalizer"/> so "PMingLiU", "pmingliu" and
    /// "p ming liu" all resolve to the same entry. Original display names are
    /// preserved on the values for logging.
    /// </summary>
    public sealed class FontMappingTable
    {
        private readonly Dictionary<string, string> _capabilities;
        private readonly Dictionary<string, GoogleFontSpec> _google;
        private readonly Dictionary<string, GitHubFontSpec> _github;
        private readonly Dictionary<string, FontFallback> _fallbacks;

        public FontMappingTable(
            IReadOnlyDictionary<string, string>? capabilities = null,
            IReadOnlyDictionary<string, FontFallback>? fallbacks = null,
            IReadOnlyDictionary<string, GoogleFontSpec>? googleFonts = null,
            IReadOnlyDictionary<string, GitHubFontSpec>? githubFonts = null)
        {
            _capabilities = new Dictionary<string, string>(StringComparer.Ordinal);
            _google = new Dictionary<string, GoogleFontSpec>(StringComparer.Ordinal);
            _github = new Dictionary<string, GitHubFontSpec>(StringComparer.Ordinal);
            _fallbacks = new Dictionary<string, FontFallback>(StringComparer.Ordinal);

            if (capabilities != null)
            {
                foreach (var kv in capabilities)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
                    _capabilities[FontNameNormalizer.Normalize(kv.Key)] = kv.Value;
                }
            }
            if (googleFonts != null)
            {
                foreach (var kv in googleFonts)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null) continue;
                    _google[FontNameNormalizer.Normalize(kv.Key)] = kv.Value;
                }
            }
            if (githubFonts != null)
            {
                foreach (var kv in githubFonts)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null) continue;
                    if (string.IsNullOrWhiteSpace(kv.Value.Owner) || string.IsNullOrWhiteSpace(kv.Value.Repo)) continue;
                    _github[FontNameNormalizer.Normalize(kv.Key)] = kv.Value;
                }
            }
            if (fallbacks != null)
            {
                foreach (var kv in fallbacks)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null || string.IsNullOrWhiteSpace(kv.Value.Url)) continue;
                    _fallbacks[FontNameNormalizer.Normalize(kv.Key)] = kv.Value;
                }
            }
        }

        public static FontMappingTable Empty { get; } = new FontMappingTable();

        public int EntryCount
        {
            get
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var k in _capabilities.Keys) keys.Add(k);
                foreach (var k in _google.Keys) keys.Add(k);
                foreach (var k in _github.Keys) keys.Add(k);
                foreach (var k in _fallbacks.Keys) keys.Add(k);
                return keys.Count;
            }
        }

        public bool TryGetCapability(string fontFamily, out string capabilityName)
            => _capabilities.TryGetValue(FontNameNormalizer.Normalize(fontFamily), out capabilityName!);

        public bool TryGetGoogleFont(string fontFamily, out GoogleFontSpec spec)
            => _google.TryGetValue(FontNameNormalizer.Normalize(fontFamily), out spec!);

        public bool TryGetGitHubFont(string fontFamily, out GitHubFontSpec spec)
            => _github.TryGetValue(FontNameNormalizer.Normalize(fontFamily), out spec!);

        public bool TryGetFallback(string fontFamily, out FontFallback fallback)
            => _fallbacks.TryGetValue(FontNameNormalizer.Normalize(fontFamily), out fallback!);

        public bool HasAnyMappingFor(string fontFamily)
        {
            string key = FontNameNormalizer.Normalize(fontFamily);
            return _capabilities.ContainsKey(key)
                || _google.ContainsKey(key)
                || _github.ContainsKey(key)
                || _fallbacks.ContainsKey(key);
        }
    }

    /// <summary>
    /// Loads <see cref="FontMappingTable"/> from a JSON file. Missing or
    /// malformed files yield an empty table rather than throwing — the
    /// resolver simply finds nothing to provision.
    /// </summary>
    public static class FontMappingLoader
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public static FontMappingTable LoadFromFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return FontMappingTable.Empty;
                string json = File.ReadAllText(path);
                return LoadFromJson(json);
            }
            catch
            {
                return FontMappingTable.Empty;
            }
        }

        public static FontMappingTable LoadFromJson(string json)
        {
            try
            {
                var file = JsonSerializer.Deserialize<FontMappingFile>(json, Options);
                if (file == null) return FontMappingTable.Empty;
                return new FontMappingTable(
                    capabilities: file.WindowsCapabilities,
                    fallbacks: file.ExternalFallbacks,
                    googleFonts: file.GoogleFonts,
                    githubFonts: file.GitHubFonts);
            }
            catch
            {
                return FontMappingTable.Empty;
            }
        }
    }
}

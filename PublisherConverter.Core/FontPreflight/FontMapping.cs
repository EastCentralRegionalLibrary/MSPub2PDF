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

    /// <summary>Raw on-disk shape of FontMapping.json.</summary>
    public sealed class FontMappingFile
    {
        [JsonPropertyName("windowsCapabilities")]
        public Dictionary<string, string> WindowsCapabilities { get; set; } = new Dictionary<string, string>();

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
        private readonly Dictionary<string, FontFallback> _fallbacks;

        public FontMappingTable(
            IReadOnlyDictionary<string, string> capabilities,
            IReadOnlyDictionary<string, FontFallback> fallbacks)
        {
            _capabilities = new Dictionary<string, string>(StringComparer.Ordinal);
            _fallbacks = new Dictionary<string, FontFallback>(StringComparer.Ordinal);

            if (capabilities != null)
            {
                foreach (var kv in capabilities)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
                    _capabilities[FontNameNormalizer.Normalize(kv.Key)] = kv.Value;
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

        public static FontMappingTable Empty { get; } =
            new FontMappingTable(new Dictionary<string, string>(), new Dictionary<string, FontFallback>());

        public int EntryCount
        {
            get
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var k in _capabilities.Keys) keys.Add(k);
                foreach (var k in _fallbacks.Keys) keys.Add(k);
                return keys.Count;
            }
        }

        public bool TryGetCapability(string fontFamily, out string capabilityName)
            => _capabilities.TryGetValue(FontNameNormalizer.Normalize(fontFamily), out capabilityName!);

        public bool TryGetFallback(string fontFamily, out FontFallback fallback)
            => _fallbacks.TryGetValue(FontNameNormalizer.Normalize(fontFamily), out fallback!);

        public bool HasAnyMappingFor(string fontFamily)
        {
            string key = FontNameNormalizer.Normalize(fontFamily);
            return _capabilities.ContainsKey(key) || _fallbacks.ContainsKey(key);
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
                return new FontMappingTable(file.WindowsCapabilities, file.ExternalFallbacks);
            }
            catch
            {
                return FontMappingTable.Empty;
            }
        }
    }
}

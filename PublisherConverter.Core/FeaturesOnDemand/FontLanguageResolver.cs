using System;
using System.Collections.Generic;

namespace PublisherConverter.Core.FeaturesOnDemand
{
    /// <summary>
    /// Maps a missing font family to the Windows LanguageFeatures script token
    /// used in the UUP package name (e.g. "Thai", "Jpan", "Hant", "Cans").
    ///
    /// The mapping is derived from the existing <c>windowsCapabilities</c> table
    /// rather than a second hand-maintained list: a capability such as
    /// <c>Language.Fonts.Hant~~~und-HANT~0.0.1.0</c> already encodes the script
    /// token (<c>Hant</c>), and that token is exactly the one that appears in
    /// <c>Microsoft-Windows-LanguageFeatures-Fonts-Hant-Package</c>. Keeping a
    /// single source of truth means a font added to the capability map is
    /// automatically eligible for the Features-on-Demand fallback too.
    /// </summary>
    public sealed class FontLanguageResolver
    {
        private const string CapabilityPrefix = "Language.Fonts.";

        private readonly FontMappingTable _mappings;

        public FontLanguageResolver(FontMappingTable mappings)
        {
            _mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
        }

        /// <summary>
        /// Resolves the language/script token for one font, or false when the
        /// font has no capability mapping (and therefore no FoD language).
        /// </summary>
        public bool TryGetLanguage(string fontFamily, out string language)
        {
            language = string.Empty;
            if (!_mappings.TryGetCapability(fontFamily, out var capability)) return false;

            string? token = ParseScriptToken(capability);
            if (token == null) return false;

            language = token;
            return true;
        }

        /// <summary>
        /// Groups a batch of missing fonts by language token. Fonts without a
        /// mapping are dropped (they cannot be served by FoD). The value lists
        /// preserve which requested fonts each language CAB is expected to
        /// satisfy so the pipeline can mark them resolved once installed.
        /// </summary>
        public IReadOnlyDictionary<string, List<string>> GroupByLanguage(IEnumerable<string> fonts)
        {
            var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (fonts == null) return grouped;

            foreach (var font in fonts)
            {
                if (string.IsNullOrWhiteSpace(font)) continue;
                if (!TryGetLanguage(font, out var language)) continue;

                if (!grouped.TryGetValue(language, out var list))
                {
                    list = new List<string>();
                    grouped[language] = list;
                }
                if (!list.Contains(font, StringComparer.OrdinalIgnoreCase)) list.Add(font);
            }
            return grouped;
        }

        /// <summary>
        /// Extracts the script token from a capability name, e.g.
        /// <c>Language.Fonts.Jpan~~~und-JPAN~0.0.1.0</c> → <c>Jpan</c>. Returns
        /// null for any string that is not a <c>Language.Fonts.*</c> capability.
        /// </summary>
        public static string? ParseScriptToken(string? capability)
        {
            if (string.IsNullOrEmpty(capability)) return null;

            int idx = capability.IndexOf(CapabilityPrefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            int start = idx + CapabilityPrefix.Length;
            int end = start;
            while (end < capability.Length && capability[end] != '~') end++;

            string token = capability.Substring(start, end - start).Trim();
            return token.Length == 0 ? null : token;
        }
    }
}

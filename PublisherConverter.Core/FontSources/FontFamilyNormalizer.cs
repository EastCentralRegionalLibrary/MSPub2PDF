using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PublisherConverter.Core.FontSources
{
    /// <summary>
    /// Splits a raw requested font name into a canonical family plus its style
    /// tokens, so source resolution targets the family and style selection is
    /// re-applied afterwards. Case-insensitive, whitespace/punctuation tolerant,
    /// alias-aware, and driven by the configured style-suffix list.
    /// </summary>
    public sealed class FontFamilyNormalizer
    {
        // Single style tokens (normalized: lower-case, no separators). Width
        // tokens like "condensed"/"expanded" are intentionally NOT here — they
        // are usually part of the family name (e.g. "Roboto Condensed"). Add them
        // via config when a source needs them stripped.
        // Clear weight/slant tokens only. "Roman"/"Book"/"Normal" are deliberately
        // excluded — they collide with real family names (e.g. "Times New Roman").
        private static readonly HashSet<string> DefaultStyleTokens = new(StringComparer.Ordinal)
        {
            "regular",
            "bold", "italic", "oblique",
            "medium", "semibold", "demibold", "light", "thin",
            "black", "heavy", "extrabold", "ultrabold", "extralight", "ultralight",
        };

        private readonly HashSet<string> _styleTokens;
        private readonly Func<string, string> _aliasResolver;

        public FontFamilyNormalizer(FontSourceConfiguration config)
            : this(config.StyleSuffixes, config.ResolveAlias)
        {
        }

        public FontFamilyNormalizer(IEnumerable<string>? styleSuffixes = null, Func<string, string>? aliasResolver = null)
        {
            _styleTokens = new HashSet<string>(DefaultStyleTokens, StringComparer.Ordinal);
            if (styleSuffixes != null)
            {
                foreach (var s in styleSuffixes)
                {
                    string key = Canon(s);
                    if (key.Length > 0) _styleTokens.Add(key);
                }
            }
            _aliasResolver = aliasResolver ?? (x => x);
        }

        /// <summary>Parses a requested name into a structured <see cref="FontRequest"/>.</summary>
        public FontRequest Parse(string requestedName)
        {
            string raw = requestedName ?? string.Empty;
            var tokens = Tokenize(raw);

            // Pop trailing style tokens (decomposing combined ones like "BoldItalic").
            var styles = new List<string>();
            int end = tokens.Count;
            while (end > 0)
            {
                var decomposed = MatchStyle(tokens[end - 1]);
                if (decomposed == null) break;
                // Keep document order: prepend this token's styles ahead of later ones.
                styles.InsertRange(0, decomposed);
                end--;
            }

            // Never strip the family away entirely (e.g. a font literally named "Black").
            if (end == 0)
            {
                end = tokens.Count;
                styles.Clear();
            }

            string family = string.Join(" ", tokens.Take(end));
            family = _aliasResolver(family);

            var canonicalStyles = CanonicalStyles(styles);

            return new FontRequest
            {
                RequestedName = raw,
                NormalizedFamily = CollapseWhitespace(family),
                RequestedStyles = canonicalStyles,
            };
        }

        /// <summary>Maps a style list to a single filename token (Regular/Bold/Italic/BoldItalic/Medium…).</summary>
        public static string StyleFileToken(IReadOnlyList<string> styles)
        {
            if (styles == null || styles.Count == 0) return "Regular";

            bool italic = styles.Any(s => s.Equals("Italic", StringComparison.OrdinalIgnoreCase) || s.Equals("Oblique", StringComparison.OrdinalIgnoreCase));
            string? weight = styles.FirstOrDefault(s =>
                !s.Equals("Italic", StringComparison.OrdinalIgnoreCase) &&
                !s.Equals("Oblique", StringComparison.OrdinalIgnoreCase) &&
                !s.Equals("Regular", StringComparison.OrdinalIgnoreCase) &&
                !s.Equals("Normal", StringComparison.OrdinalIgnoreCase));

            if (weight != null && italic) return weight + "Italic";
            if (weight != null) return weight;
            if (italic) return "Italic";
            return "Regular";
        }

        public static string CollapseWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            bool prevSpace = false;
            foreach (char c in s.Trim())
            {
                if (char.IsWhiteSpace(c))
                {
                    if (!prevSpace) sb.Append(' ');
                    prevSpace = true;
                }
                else
                {
                    sb.Append(c);
                    prevSpace = false;
                }
            }
            return sb.ToString();
        }

        // ---- internals ----

        private static List<string> Tokenize(string raw)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();
            foreach (char c in raw)
            {
                if (char.IsWhiteSpace(c) || c == '-' || c == '_')
                {
                    if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                }
                else
                {
                    sb.Append(c);
                }
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());
            return tokens;
        }

        // Returns the canonical style names a token represents, or null if it is
        // not a style token at all.
        private List<string>? MatchStyle(string token)
        {
            string key = Canon(token);
            if (key.Length == 0) return null;

            if (_styleTokens.Contains(key)) return new List<string> { CanonName(key) };

            // Try to split a combined token (e.g. "bolditalic", "boldoblique").
            for (int i = 2; i < key.Length - 1; i++)
            {
                string left = key.Substring(0, i);
                string right = key.Substring(i);
                if (_styleTokens.Contains(left) && _styleTokens.Contains(right))
                {
                    return new List<string> { CanonName(left), CanonName(right) };
                }
            }
            return null;
        }

        private static List<string> CanonicalStyles(List<string> styles)
        {
            // De-dupe, drop "Regular"/"Normal" (implied), preserve order.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var s in styles)
            {
                if (s.Equals("Regular", StringComparison.OrdinalIgnoreCase) || s.Equals("Normal", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(s)) result.Add(s);
            }
            return result;
        }

        private static string Canon(string token)
        {
            var sb = new StringBuilder(token.Length);
            foreach (char c in token)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static string CanonName(string lowerKey) => lowerKey switch
        {
            "regular" or "normal" => "Regular",
            "bold" => "Bold",
            "italic" => "Italic",
            "oblique" => "Oblique",
            "medium" => "Medium",
            "semibold" or "demibold" => "SemiBold",
            "light" => "Light",
            "thin" => "Thin",
            "black" or "heavy" => "Black",
            "extrabold" or "ultrabold" => "ExtraBold",
            "extralight" or "ultralight" => "ExtraLight",
            "roman" or "book" => "Regular",
            _ => char.ToUpperInvariant(lowerKey[0]) + lowerKey.Substring(1),
        };
    }
}

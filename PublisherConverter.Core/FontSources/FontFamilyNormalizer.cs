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

        // Weight modifier words that are only meaningful joined to an adjacent
        // base weight: "Extra Bold" → ExtraBold, "Semi Bold" → SemiBold, … A
        // modifier is joined ONLY when the concatenated form is already a known
        // style token, so real family words are never consumed ("Times New
        // Roman", "Franklin Gothic Book" are untouched) and the set stays in
        // sync with the configured style-suffix list rather than per-font logic.
        private static readonly HashSet<string> WeightModifierTokens = new(StringComparer.Ordinal)
        {
            "extra", "semi", "demi", "ultra",
        };

        private static readonly HashSet<string> BaseWeightTokens = new(StringComparer.Ordinal)
        {
            "bold", "light", "black",
        };

        private readonly HashSet<string> _styleTokens;
        private readonly List<string> _sortedStyleTokens;
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
            _sortedStyleTokens = _styleTokens.OrderByDescending(s => s.Length).ToList();
            _aliasResolver = aliasResolver ?? (x => x);
        }

        /// <summary>Parses a requested name into a structured <see cref="FontRequest"/>.</summary>
        public FontRequest Parse(string requestedName)
        {
            string raw = requestedName ?? string.Empty;
            var tokens = Tokenize(raw);

            // Pop trailing style tokens (peeling from right to left).
            var styles = new List<string>();
            int end = tokens.Count;
            while (end > 0)
            {
                var peeled = PeelStyles(tokens[end - 1], out string remaining);
                if (peeled.Count == 0)
                {
                    // A lone modifier word directly before a just-peeled base
                    // weight is a two-word compound weight ("Extra Bold" →
                    // ExtraBold, "Semi Bold" → SemiBold): consume it and
                    // upgrade the peeled style to the joined canonical form.
                    if (styles.Count > 0 && TryJoinWeightModifier(tokens[end - 1], styles[0], out string joined))
                    {
                        styles[0] = joined;
                        end--;
                        continue;
                    }
                    break;
                }

                // Keep document order: prepend this token's styles ahead of later ones.
                styles.InsertRange(0, peeled);

                if (remaining.Length > 0)
                {
                    // We found styles but the token still has content; this must be the family name.
                    tokens[end - 1] = remaining;
                    break;
                }

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

        // Joins "Extra"/"Semi"/"Demi"/"Ultra" with the base weight peeled
        // immediately to its right ("bold"/"light"/"black") when — and only
        // when — the concatenation is a known style token, yielding that
        // token's canonical name. Anything else (including already-compound
        // styles like ExtraBold) is left alone.
        private bool TryJoinWeightModifier(string token, string peeledStyle, out string joined)
        {
            joined = string.Empty;
            string modifier = Canon(token);
            if (!WeightModifierTokens.Contains(modifier)) return false;

            string baseWeight = Canon(peeledStyle);
            if (!BaseWeightTokens.Contains(baseWeight)) return false;

            string combined = modifier + baseWeight;
            if (!_styleTokens.Contains(combined)) return false;

            joined = CanonName(combined);
            return true;
        }

        private List<string> PeelStyles(string token, out string remaining)
        {
            var peeled = new List<string>();
            string current = token;

            while (true)
            {
                current = TrimTrailingSeparators(current);
                if (current.Length == 0) break;

                string key = Canon(current);
                bool matched = false;

                foreach (var styleKey in _sortedStyleTokens)
                {
                    if (key.EndsWith(styleKey, StringComparison.Ordinal))
                    {
                        int styleStartIdx = FindStyleStartIndex(current, styleKey);
                        if (styleStartIdx >= 0)
                        {
                            peeled.Insert(0, CanonName(styleKey));
                            current = current.Substring(0, styleStartIdx);
                            matched = true;
                            break;
                        }
                    }
                }

                if (!matched) break;
            }

            remaining = current;
            return peeled;
        }

        private static string TrimTrailingSeparators(string s)
        {
            int i = s.Length - 1;
            while (i >= 0 && (char.IsWhiteSpace(s[i]) || s[i] == '-' || s[i] == '_'))
            {
                i--;
            }
            return s.Substring(0, i + 1);
        }

        private static int FindStyleStartIndex(string token, string styleKey)
        {
            int styleCharIdx = styleKey.Length - 1;
            for (int i = token.Length - 1; i >= 0; i--)
            {
                char c = token[i];
                if (!char.IsLetterOrDigit(c)) continue;

                if (char.ToLowerInvariant(c) == styleKey[styleCharIdx])
                {
                    styleCharIdx--;
                    if (styleCharIdx < 0) return i;
                }
                else
                {
                    return -1;
                }
            }
            return -1;
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

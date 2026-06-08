using System;
using System.Text;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Canonicalizes font family names for comparison: strips whitespace and
    /// hyphens, lower-cases everything. So "Lemon Cookie Bold", "lemon cookie
    /// bold", and "LemonCookie-Bold" all collapse to the same key. Used by the
    /// cache and the installed-font provider's lookup table.
    /// </summary>
    public static class FontNameNormalizer
    {
        public static string Normalize(string family)
        {
            if (string.IsNullOrEmpty(family)) return string.Empty;

            var sb = new StringBuilder(family.Length);
            foreach (char c in family)
            {
                if (char.IsWhiteSpace(c) || c == '-') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}

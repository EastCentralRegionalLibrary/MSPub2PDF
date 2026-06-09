using System;
using System.Collections.Generic;
using System.Text;

namespace PublisherConverter.Core.FontSources
{
    /// <summary>
    /// Expands the <c>{placeholder}</c> tokens used in configured path/URL
    /// templates. Keeping this in one place means new sources express their
    /// layout in FontSources.json instead of in resolver code.
    ///
    /// Supported tokens: {licenseDir} {slug} {Family} {FamilyNoSpace} {family}
    /// {Style} {style} {query} {owner} {repo} {branch} {tag}.
    /// </summary>
    public static class UrlTemplate
    {
        public static string Expand(string template, IReadOnlyDictionary<string, string> values)
        {
            if (string.IsNullOrEmpty(template)) return string.Empty;
            var sb = new StringBuilder(template.Length + 16);
            int i = 0;
            while (i < template.Length)
            {
                char c = template[i];
                if (c == '{')
                {
                    int end = template.IndexOf('}', i + 1);
                    if (end > i)
                    {
                        string key = template.Substring(i + 1, end - i - 1);
                        sb.Append(values.TryGetValue(key, out var v) ? v : string.Empty);
                        i = end + 1;
                        continue;
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>Builds the standard placeholder bag for a family/style.</summary>
        public static Dictionary<string, string> Values(string family, string style, string? licenseDir = null, RepoSpec? repo = null)
        {
            string noSpace = family.Replace(" ", string.Empty);
            var d = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Family"] = family,
                ["FamilyNoSpace"] = noSpace,
                ["family"] = family.ToLowerInvariant(),
                ["slug"] = Slug(family),
                ["Style"] = style,
                ["style"] = style.ToLowerInvariant(),
                ["licenseDir"] = licenseDir ?? string.Empty,
            };
            if (repo != null)
            {
                d["owner"] = repo.Owner;
                d["repo"] = repo.Repo;
                d["branch"] = repo.Branch;
                d["tag"] = repo.Tag ?? string.Empty;
            }
            return d;
        }

        /// <summary>Lower-case, alphanumeric-only slug (e.g. "Open Sans" → "opensans").</summary>
        public static string Slug(string family)
        {
            var sb = new StringBuilder(family.Length);
            foreach (char c in family)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}

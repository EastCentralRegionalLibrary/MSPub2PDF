using System;
using System.Collections.Generic;
using PublisherConverter.Core.FontSources;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Platform-neutral two-key index over installed font face names, applying
    /// the style-gated matching rule shared by the installed-font providers:
    ///
    ///   1. <b>Exact full-name check first</b> — the request's normalized form
    ///      against the full face-name index (the source of truth). This is what
    ///      protects face-per-family fonts ("Franklin Gothic Book",
    ///      "Bahnschrift SemiBold"): they match on their exact name and never
    ///      reach the fallback, so they are never collapsed into a parent family.
    ///   2. <b>Family fallback, only for bare requests</b> — style tokens are
    ///      parsed off the requested name with the same style-aware
    ///      <see cref="FontFamilyNormalizer"/> acquisition uses (one source of
    ///      truth for "what is a style token"). If the request carried no style
    ///      tokens, its family key is checked against the style-stripped family
    ///      keys of the installed faces. If the request DID carry style tokens,
    ///      there is no fallback: an exact miss on a styled name is genuinely
    ///      missing and must trigger acquisition — a family match there would
    ///      silently ship the wrong face.
    ///
    /// Documented decision: the bare-family fallback intentionally treats a
    /// family as available when ANY of its faces is installed, even when no
    /// bare/regular face exists (e.g. "Lucida Sans" satisfied by only
    /// "Lucida Sans Demibold"). Large families register per-face with no bare
    /// entry, so exact-only matching produced false "missing font" reports for
    /// installed fonts; presence-of-any-face is the correct pre-flight tradeoff
    /// and the out-of-process worker's exit code remains the backstop. The
    /// fallback never applies to styled requests.
    /// </summary>
    public sealed class InstalledFontIndex
    {
        private readonly HashSet<string> _fullNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _familyKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly FontFamilyNormalizer _normalizer;

        public InstalledFontIndex(IEnumerable<string> faceNames, FontFamilyNormalizer? normalizer = null)
        {
            if (faceNames == null) throw new ArgumentNullException(nameof(faceNames));
            _normalizer = normalizer ?? new FontFamilyNormalizer();

            foreach (var face in faceNames)
            {
                if (string.IsNullOrWhiteSpace(face)) continue;
                _fullNames.Add(FontNameNormalizer.Normalize(face));
                _familyKeys.Add(FontNameNormalizer.Normalize(_normalizer.Parse(face).NormalizedFamily));
            }
        }

        /// <summary>Full normalized face names — the exact-match source of truth.</summary>
        public IReadOnlyCollection<string> NormalizedFontNames => _fullNames;

        public bool IsInstalled(string requestedName)
        {
            if (string.IsNullOrEmpty(requestedName)) return false;

            // 1. Exact face-name hit.
            if (_fullNames.Contains(FontNameNormalizer.Normalize(requestedName))) return true;

            // 2. Family fallback — bare-family requests only.
            var request = _normalizer.Parse(requestedName);
            if (request.RequestedStyles.Count > 0) return false;
            return _familyKeys.Contains(FontNameNormalizer.Normalize(request.NormalizedFamily));
        }
    }
}

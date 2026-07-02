using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Microsoft.Win32;
using PublisherConverter.Core.FontSources;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Reads the installed-font list from the standard Windows font registry
    /// keys (machine-scope and current-user). Each value name looks like
    /// "Family (TrueType)" or "Family A &amp; Family B (TrueType)"; we strip the
    /// parenthesised suffix and split on ampersand so each face lands in the
    /// lookup on its own.
    ///
    /// Matching is delegated to the platform-neutral
    /// <see cref="InstalledFontIndex"/>: an exact full-name check first, then a
    /// style-gated family fallback so multi-face families that register only
    /// per-face ("Lucida Sans Regular"/"… Demibold" with no bare "Lucida Sans"
    /// entry) still satisfy a bare-family request, while styled requests never
    /// over-match. See the index's doc comment for the full decision.
    ///
    /// Built once and used many times: enumeration happens in the constructor
    /// and IsInstalled is a couple of HashSet hits, so the orchestrator can call
    /// it for every font in every document without paying registry I/O each time.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsRegistryFontProvider : IRefreshableInstalledFontProvider
    {
        private const string FontsSubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

        private readonly FontFamilyNormalizer _normalizer;
        private volatile InstalledFontIndex _index;

        /// <param name="normalizer">
        /// Style-aware normalizer shared with the acquisition side so detection
        /// and acquisition agree on what a style token is. Defaults to the
        /// built-in style-token set when no configured instance is supplied.
        /// </param>
        public WindowsRegistryFontProvider(FontFamilyNormalizer? normalizer = null)
        {
            _normalizer = normalizer ?? new FontFamilyNormalizer();
            _index = BuildIndex(_normalizer);
        }

        public bool IsInstalled(string family)
        {
            if (string.IsNullOrEmpty(family)) return false;
            return _index.IsInstalled(family);
        }

        /// <summary>
        /// Re-enumerates the machine and current-user font registrations. Cheap
        /// enough to call after a font install so the next lookup sees a font
        /// that landed under its real family name.
        /// </summary>
        public void Refresh()
        {
            _index = BuildIndex(_normalizer);
        }

        public IReadOnlyCollection<string> NormalizedFontNames => _index.NormalizedFontNames;

        private static InstalledFontIndex BuildIndex(FontFamilyNormalizer normalizer)
        {
            var faces = new List<string>();
            EnumerateRegistry(Registry.LocalMachine, faces);
            EnumerateRegistry(Registry.CurrentUser, faces);
            return new InstalledFontIndex(faces, normalizer);
        }

        private static void EnumerateRegistry(RegistryKey root, List<string> sink)
        {
            try
            {
                using var key = root.OpenSubKey(FontsSubKey);
                if (key == null) return;

                foreach (var valueName in key.GetValueNames())
                {
                    if (string.IsNullOrEmpty(valueName)) continue;
                    foreach (var face in ExtractFamilyNames(valueName))
                    {
                        sink.Add(face);
                    }
                }
            }
            catch
            {
                // Registry inaccessible / permission denied — silently skip.
                // The auditor will simply report more fonts as missing in that
                // (unlikely) case; the worker exit-code backstop still covers us.
            }
        }

        private static IEnumerable<string> ExtractFamilyNames(string registryValue)
        {
            int paren = registryValue.IndexOf('(');
            string trimmed = paren >= 0
                ? registryValue.Substring(0, paren).Trim()
                : registryValue.Trim();

            foreach (var part in trimmed.Split('&'))
            {
                string name = part.Trim();
                if (name.Length > 0) yield return name;
            }
        }
    }
}

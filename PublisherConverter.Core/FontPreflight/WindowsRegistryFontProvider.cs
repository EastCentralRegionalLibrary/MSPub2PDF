using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Reads the installed-font list from the standard Windows font registry
    /// keys (machine-scope and current-user). Each value name looks like
    /// "Family (TrueType)" or "Family A &amp; Family B (TrueType)"; we strip the
    /// parenthesised suffix and split on ampersand so each family lands in the
    /// lookup table on its own.
    ///
    /// Built once and used many times: enumeration happens in the constructor
    /// and IsInstalled is a HashSet hit, so the orchestrator can call it for
    /// every font in every document without paying registry I/O each time.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsRegistryFontProvider : IInstalledFontProvider
    {
        private const string FontsSubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

        private readonly HashSet<string> _installed;

        public WindowsRegistryFontProvider()
        {
            _installed = new HashSet<string>(StringComparer.Ordinal);
            EnumerateRegistry(Registry.LocalMachine, _installed);
            EnumerateRegistry(Registry.CurrentUser, _installed);
        }

        public bool IsInstalled(string family)
        {
            if (string.IsNullOrEmpty(family)) return false;
            return _installed.Contains(FontNameNormalizer.Normalize(family));
        }

        public IReadOnlyCollection<string> NormalizedFontNames => _installed;

        private static void EnumerateRegistry(RegistryKey root, HashSet<string> sink)
        {
            try
            {
                using var key = root.OpenSubKey(FontsSubKey);
                if (key == null) return;

                foreach (var valueName in key.GetValueNames())
                {
                    if (string.IsNullOrEmpty(valueName)) continue;
                    foreach (var family in ExtractFamilyNames(valueName))
                    {
                        sink.Add(FontNameNormalizer.Normalize(family));
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

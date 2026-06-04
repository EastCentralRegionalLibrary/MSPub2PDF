using System;
using System.Collections.Generic;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Wraps an <see cref="IInstalledFontProvider"/> and memoizes its answers,
    /// keyed by the normalized family name. Callers in the orchestrator hit
    /// this once per distinct font across a whole batch — a 100-file run that
    /// shares fonts only pays the provider lookup cost a handful of times.
    /// </summary>
    public sealed class FontAvailabilityCache
    {
        private readonly IInstalledFontProvider _provider;
        private readonly Dictionary<string, bool> _cache = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly object _lock = new object();

        public FontAvailabilityCache(IInstalledFontProvider provider)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        }

        public bool IsInstalled(string family)
        {
            string normalized = FontNameNormalizer.Normalize(family);

            lock (_lock)
            {
                if (_cache.TryGetValue(normalized, out bool cached)) return cached;
            }

            bool installed = _provider.IsInstalled(family);

            lock (_lock)
            {
                _cache[normalized] = installed;
            }

            return installed;
        }

        /// <summary>
        /// Drops cached answers for a single font family. The future
        /// auto-installer will call this after fetching a font so the next
        /// audit pass sees the freshly-installed family.
        /// </summary>
        public void Invalidate(string family)
        {
            string normalized = FontNameNormalizer.Normalize(family);
            lock (_lock)
            {
                _cache.Remove(normalized);
            }
        }
    }
}

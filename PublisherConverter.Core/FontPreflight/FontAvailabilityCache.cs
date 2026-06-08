using System;
using System.Collections.Generic;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Wraps an <see cref="IInstalledFontProvider"/> and memoizes its answers,
    /// keyed by the normalized family name. Callers in the orchestrator hit
    /// this once per distinct font across a whole batch — a 100-file run that
    /// shares fonts only pays the provider lookup cost a handful of times.
    ///
    /// Two layers of truth:
    ///   * _provisioned — families the auto-resolver verified-installed during
    ///     this session. Always "installed", and survives a provider refresh.
    ///     This is how a downloadable fallback (installed under a different
    ///     family name than the one the document asked for) is treated as
    ///     resolving the original missing font.
    ///   * _memo — memoized answers from the underlying provider. Cleared on
    ///     <see cref="RefreshProvider"/> so freshly-installed fonts that landed
    ///     under their real name are re-read.
    /// </summary>
    public sealed class FontAvailabilityCache
    {
        private readonly IInstalledFontProvider _provider;
        private readonly Dictionary<string, bool> _memo = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly HashSet<string> _provisioned = new HashSet<string>(StringComparer.Ordinal);
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
                if (_provisioned.Contains(normalized)) return true;
                if (_memo.TryGetValue(normalized, out bool cached)) return cached;
            }

            bool installed = _provider.IsInstalled(family);

            lock (_lock)
            {
                _memo[normalized] = installed;
            }

            return installed;
        }

        /// <summary>
        /// Marks a font family as available following a verified install. Used
        /// by the auto-resolver after a successful capability/fallback install
        /// so the very next availability check (this file or a later one)
        /// treats the original missing font as resolved — even when the
        /// installed substitute carries a different family name.
        /// </summary>
        public void MarkInstalled(string family)
        {
            string normalized = FontNameNormalizer.Normalize(family);
            lock (_lock)
            {
                _provisioned.Add(normalized);
                _memo[normalized] = true;
            }
        }

        /// <summary>
        /// Drops cached answers for a single font family, including any
        /// provisioned override, forcing the next check to re-ask the provider.
        /// </summary>
        public void Invalidate(string family)
        {
            string normalized = FontNameNormalizer.Normalize(family);
            lock (_lock)
            {
                _memo.Remove(normalized);
                _provisioned.Remove(normalized);
            }
        }

        /// <summary>
        /// Re-reads the underlying OS font state (if the provider supports it)
        /// and clears memoized provider answers, so fonts installed under their
        /// real family name this session are picked up. Verified provisioned
        /// entries (<see cref="MarkInstalled"/>) are preserved.
        /// </summary>
        public void RefreshProvider()
        {
            if (_provider is IRefreshableInstalledFontProvider refreshable)
            {
                refreshable.Refresh();
            }
            lock (_lock)
            {
                _memo.Clear();
            }
        }
    }
}

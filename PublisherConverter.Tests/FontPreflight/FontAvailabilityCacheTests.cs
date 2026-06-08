using System.Collections.Generic;
using PublisherConverter.Core;
using Xunit;

namespace PublisherConverter.Tests
{
    public sealed class FontAvailabilityCacheTests
    {
        private sealed class TrackingProvider : IRefreshableInstalledFontProvider
        {
            public HashSet<string> Installed { get; } = new HashSet<string>();
            public int Lookups { get; private set; }
            public int Refreshes { get; private set; }

            public bool IsInstalled(string family)
            {
                Lookups++;
                return Installed.Contains(FontNameNormalizer.Normalize(family));
            }

            public void Refresh() => Refreshes++;
        }

        [Fact]
        public void MarkInstalled_overrides_provider_until_invalidated()
        {
            var provider = new TrackingProvider();
            var cache = new FontAvailabilityCache(provider);

            Assert.False(cache.IsInstalled("MissingFont"));
            cache.MarkInstalled("MissingFont");

            Assert.True(cache.IsInstalled("MissingFont"));
            // Even after a refresh, the provisioned marker stays.
            cache.RefreshProvider();
            Assert.True(cache.IsInstalled("MissingFont"));

            // Invalidate falls back to the provider — provider still says no.
            cache.Invalidate("MissingFont");
            Assert.False(cache.IsInstalled("MissingFont"));
        }

        [Fact]
        public void RefreshProvider_clears_memo_and_calls_provider_refresh()
        {
            var provider = new TrackingProvider();
            var cache = new FontAvailabilityCache(provider);

            Assert.False(cache.IsInstalled("LateFont"));
            Assert.Equal(1, provider.Lookups);

            // Same query: should be memoized.
            Assert.False(cache.IsInstalled("LateFont"));
            Assert.Equal(1, provider.Lookups);

            // Provider state changes (font installed under its real name).
            provider.Installed.Add(FontNameNormalizer.Normalize("LateFont"));
            cache.RefreshProvider();

            Assert.True(cache.IsInstalled("LateFont"));
            Assert.Equal(2, provider.Lookups);
            Assert.Equal(1, provider.Refreshes);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core;
using Xunit;

namespace PublisherConverter.Tests
{
    public sealed class FontResolverTests
    {
        // Fake provider whose answers can flip between calls (simulating a
        // post-install RefreshProvider catching up with reality).
        private sealed class MutableFontProvider : IRefreshableInstalledFontProvider
        {
            public HashSet<string> InstalledNormalized { get; } = new HashSet<string>();
            public int RefreshCount { get; private set; }

            public bool IsInstalled(string family)
                => InstalledNormalized.Contains(FontNameNormalizer.Normalize(family));

            public void Refresh() => RefreshCount++;
        }

        // Scripted strategy: per-font outcomes specified up front.
        private sealed class ScriptedStrategy : IFontProvisioningStrategy
        {
            private readonly Dictionary<string, Func<bool>> _script;
            public List<string> AttemptedFor { get; } = new List<string>();

            public ScriptedStrategy(string name, Dictionary<string, Func<bool>> script)
            {
                Name = name;
                _script = script;
            }

            public string Name { get; }

            public bool CanResolve(string fontFamily) =>
                _script.ContainsKey(FontNameNormalizer.Normalize(fontFamily));

            public Task<bool> TryResolveAsync(string fontFamily, IList<string> log, CancellationToken cancellationToken)
            {
                AttemptedFor.Add(fontFamily);
                if (_script.TryGetValue(FontNameNormalizer.Normalize(fontFamily), out var fn))
                {
                    return Task.FromResult(fn());
                }
                return Task.FromResult(false);
            }
        }

        private static FontAvailabilityCache MakeCache(out MutableFontProvider provider)
        {
            provider = new MutableFontProvider();
            return new FontAvailabilityCache(provider);
        }

        [Fact]
        public async Task Resolves_each_font_via_a_strategy_that_succeeds()
        {
            var cache = MakeCache(out var provider);

            var strategy = new ScriptedStrategy("Test", new Dictionary<string, Func<bool>>
            {
                [FontNameNormalizer.Normalize("PMingLiU")] = () => true,
                [FontNameNormalizer.Normalize("Mangal")] = () => true,
            });
            var resolver = new FontResolver(cache, new[] { strategy });

            var outcome = await resolver.ResolveMissingFontsAsync(
                new[] { "PMingLiU", "Mangal" }, CancellationToken.None);

            Assert.Equal(new[] { "PMingLiU", "Mangal" }, outcome.Resolved);
            Assert.Empty(outcome.StillMissing);

            // Cache must be updated and the provider asked to refresh once
            // (a single refresh per resolve call, not per font).
            Assert.True(cache.IsInstalled("PMingLiU"));
            Assert.True(cache.IsInstalled("Mangal"));
            Assert.Equal(1, provider.RefreshCount);
        }

        [Fact]
        public async Task Falls_through_to_next_strategy_when_first_fails()
        {
            var cache = MakeCache(out _);

            var primary = new ScriptedStrategy("Primary", new Dictionary<string, Func<bool>>
            {
                [FontNameNormalizer.Normalize("PMingLiU")] = () => false,
            });
            var fallback = new ScriptedStrategy("Fallback", new Dictionary<string, Func<bool>>
            {
                [FontNameNormalizer.Normalize("PMingLiU")] = () => true,
            });
            var resolver = new FontResolver(cache, new IFontProvisioningStrategy[] { primary, fallback });

            var outcome = await resolver.ResolveMissingFontsAsync(new[] { "PMingLiU" }, CancellationToken.None);

            Assert.Equal(new[] { "PMingLiU" }, outcome.Resolved);
            Assert.Empty(outcome.StillMissing);
            Assert.Single(primary.AttemptedFor);
            Assert.Single(fallback.AttemptedFor);
        }

        [Fact]
        public async Task Skips_strategies_that_dont_claim_the_font_and_reports_still_missing()
        {
            var cache = MakeCache(out _);

            // Strategy only knows about Mangal.
            var strategy = new ScriptedStrategy("Test", new Dictionary<string, Func<bool>>
            {
                [FontNameNormalizer.Normalize("Mangal")] = () => true,
            });
            var resolver = new FontResolver(cache, new[] { strategy });

            var outcome = await resolver.ResolveMissingFontsAsync(
                new[] { "Lemon Cookie Bold", "Mangal" }, CancellationToken.None);

            Assert.Equal(new[] { "Mangal" }, outcome.Resolved);
            Assert.Equal(new[] { "Lemon Cookie Bold" }, outcome.StillMissing);
            Assert.DoesNotContain("Lemon Cookie Bold", strategy.AttemptedFor);
        }

        [Fact]
        public async Task Pre_existing_install_short_circuits_without_running_strategies()
        {
            var cache = MakeCache(out var provider);
            provider.InstalledNormalized.Add(FontNameNormalizer.Normalize("Arial"));

            var strategy = new ScriptedStrategy("Test", new Dictionary<string, Func<bool>>());
            var resolver = new FontResolver(cache, new[] { strategy });

            var outcome = await resolver.ResolveMissingFontsAsync(new[] { "Arial" }, CancellationToken.None);

            Assert.Equal(new[] { "Arial" }, outcome.Resolved);
            Assert.Empty(strategy.AttemptedFor);
            // No install happened, so no refresh either.
            Assert.Equal(0, provider.RefreshCount);
        }

        [Fact]
        public async Task Strategy_exception_does_not_break_resolver()
        {
            var cache = MakeCache(out _);

            var throwing = new ThrowingStrategy("Boom");
            var ok = new ScriptedStrategy("Backup", new Dictionary<string, Func<bool>>
            {
                [FontNameNormalizer.Normalize("Mangal")] = () => true,
            });
            var resolver = new FontResolver(cache, new IFontProvisioningStrategy[] { throwing, ok });

            var outcome = await resolver.ResolveMissingFontsAsync(new[] { "Mangal" }, CancellationToken.None);

            Assert.Equal(new[] { "Mangal" }, outcome.Resolved);
            Assert.Contains(outcome.Log, l => l.Contains("Boom"));
        }

        private sealed class ThrowingStrategy : IFontProvisioningStrategy
        {
            public ThrowingStrategy(string name) { Name = name; }
            public string Name { get; }
            public bool CanResolve(string fontFamily) => true;
            public Task<bool> TryResolveAsync(string fontFamily, IList<string> log, CancellationToken cancellationToken)
                => throw new InvalidOperationException("Boom");
        }
    }
}

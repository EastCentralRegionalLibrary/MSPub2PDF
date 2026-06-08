using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core;
using Xunit;

namespace PublisherConverter.Tests
{
    /// <summary>
    /// Exercises the priority order the GUI wires the resolver with:
    ///   1. Windows capability
    ///   2. Google Fonts
    ///   3. GitHub fonts
    ///   4. Direct-URL fallback
    /// Uses scripted strategy stand-ins so failures in earlier strategies
    /// don't abort the pipeline.
    /// </summary>
    public sealed class FontResolverPipelineOrderTests
    {
        private sealed class TaggingStrategy : IFontProvisioningStrategy
        {
            private readonly List<string> _trace;
            private readonly System.Collections.Generic.HashSet<string> _claims;
            private readonly System.Collections.Generic.HashSet<string> _willSucceed;

            public TaggingStrategy(string name, List<string> trace, IEnumerable<string> claims, IEnumerable<string> succeedsFor)
            {
                Name = name;
                _trace = trace;
                _claims = new System.Collections.Generic.HashSet<string>(claims, System.StringComparer.OrdinalIgnoreCase);
                _willSucceed = new System.Collections.Generic.HashSet<string>(succeedsFor, System.StringComparer.OrdinalIgnoreCase);
            }

            public string Name { get; }

            public bool CanResolve(string fontFamily) => _claims.Contains(fontFamily);

            public Task<bool> TryResolveAsync(string fontFamily, IList<string> log, CancellationToken cancellationToken)
            {
                _trace.Add($"{Name}:{fontFamily}");
                return Task.FromResult(_willSucceed.Contains(fontFamily));
            }
        }

        private sealed class CountingProvider : IRefreshableInstalledFontProvider
        {
            public bool IsInstalled(string family) => false;
            public void Refresh() { }
        }

        [Fact]
        public async Task Strategies_are_tried_in_registration_order_until_one_succeeds()
        {
            var trace = new List<string>();

            // Windows-cap doesn't claim "X"; Google claims but fails; GitHub
            // claims and succeeds; Direct never tried because GitHub won.
            var windowsCap = new TaggingStrategy("WindowsCap", trace, new string[0], new string[0]);
            var google     = new TaggingStrategy("Google",     trace, new[] { "X" }, new string[0]);
            var github     = new TaggingStrategy("GitHub",     trace, new[] { "X" }, new[] { "X" });
            var direct     = new TaggingStrategy("Direct",     trace, new[] { "X" }, new[] { "X" });

            var cache = new FontAvailabilityCache(new CountingProvider());
            var resolver = new FontResolver(cache, new IFontProvisioningStrategy[] { windowsCap, google, github, direct });

            var outcome = await resolver.ResolveMissingFontsAsync(new[] { "X" }, CancellationToken.None);

            Assert.Equal(new[] { "X" }, outcome.Resolved);
            Assert.Equal(new[] { "Google:X", "GitHub:X" }, trace);
            // Cache now treats X as installed (MarkInstalled fired).
            Assert.True(cache.IsInstalled("X"));
        }

        [Fact]
        public async Task Failure_in_one_strategy_does_not_break_pipeline_for_other_fonts()
        {
            var trace = new List<string>();

            // Two fonts, "A" and "B". WindowsCap claims A but fails; Google
            // claims A and succeeds. WindowsCap doesn't claim B; Google
            // doesn't claim B; GitHub claims B and succeeds.
            var windowsCap = new TaggingStrategy("WindowsCap", trace, new[] { "A" }, new string[0]);
            var google     = new TaggingStrategy("Google",     trace, new[] { "A" }, new[] { "A" });
            var github     = new TaggingStrategy("GitHub",     trace, new[] { "B" }, new[] { "B" });
            var direct     = new TaggingStrategy("Direct",     trace, new string[0], new string[0]);

            var cache = new FontAvailabilityCache(new CountingProvider());
            var resolver = new FontResolver(cache, new IFontProvisioningStrategy[] { windowsCap, google, github, direct });

            var outcome = await resolver.ResolveMissingFontsAsync(new[] { "A", "B" }, CancellationToken.None);

            Assert.Equal(2, outcome.Resolved.Count);
            Assert.Empty(outcome.StillMissing);
            Assert.Equal(new[] { "WindowsCap:A", "Google:A", "GitHub:B" }, trace);
        }

        [Fact]
        public async Task When_every_strategy_returns_false_the_font_stays_missing()
        {
            var trace = new List<string>();

            var windowsCap = new TaggingStrategy("WindowsCap", trace, new[] { "X" }, new string[0]);
            var google     = new TaggingStrategy("Google",     trace, new[] { "X" }, new string[0]);
            var github     = new TaggingStrategy("GitHub",     trace, new[] { "X" }, new string[0]);
            var direct     = new TaggingStrategy("Direct",     trace, new[] { "X" }, new string[0]);

            var cache = new FontAvailabilityCache(new CountingProvider());
            var resolver = new FontResolver(cache, new IFontProvisioningStrategy[] { windowsCap, google, github, direct });

            var outcome = await resolver.ResolveMissingFontsAsync(new[] { "X" }, CancellationToken.None);

            Assert.Empty(outcome.Resolved);
            Assert.Equal(new[] { "X" }, outcome.StillMissing);
            // Every strategy that claimed X was given a turn.
            Assert.Equal(new[] { "WindowsCap:X", "Google:X", "GitHub:X", "Direct:X" }, trace);
        }
    }
}

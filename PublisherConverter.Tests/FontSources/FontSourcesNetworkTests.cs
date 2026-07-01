using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FontSources;
using Xunit;

namespace PublisherConverter.Tests.FontSources
{
    /// <summary>
    /// Live-endpoint checks of the shipped FontSources.json against the real
    /// upstreams (raw.githubusercontent.com, api.github.com, dafont.com). These
    /// are excluded from CI — run on demand with:
    ///   dotnet test --filter "Category=RequiresNetwork"
    /// Rate limits and remote changes make them non-deterministic; they exist to
    /// catch upstream drift (e.g. the IBM/plex monorepo reorganization), not to
    /// gate builds.
    /// </summary>
    [Trait("Category", "RequiresNetwork")]
    public sealed class FontSourcesNetworkTests
    {
        private static FontSourceConfiguration ShippedConfig()
            => FontSourceConfiguration.LoadFromFile(
                Path.Combine(AppContext.BaseDirectory, "TestData", "FontSources", "FontSources.json"));

        private static readonly DisambiguationCallback AcceptVariableFont = (_, _, _) => Task.FromResult(0);

        [Theory]
        [InlineData("Roboto")]
        [InlineData("Open Sans")]
        public async Task Google_variable_only_family_resolves_after_accept(string family)
        {
            var config = ShippedConfig();
            var resolver = new GoogleFontsResolver(config, new HttpFontClient(), new FontLicenseEvaluator(config.Policy.License),
                variableFontCallback: AcceptVariableFont);

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(new FontFamilyNormalizer(config).Parse(family), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved, $"{family}: {result.FailureReason}");
            Assert.Contains("%5B", result.SourceUrl); // a variable font file was installed
            Assert.NotEmpty(h.Installer.InstalledFromStream);
        }

        [Fact]
        public async Task Google_static_family_resolves_without_prompt()
        {
            var config = ShippedConfig();
            int prompts = 0;
            DisambiguationCallback counting = (_, _, _) => { prompts++; return Task.FromResult(0); };
            var resolver = new GoogleFontsResolver(config, new HttpFontClient(), new FontLicenseEvaluator(config.Policy.License),
                variableFontCallback: counting);

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(new FontFamilyNormalizer(config).Parse("Lato"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved, $"Lato: {result.FailureReason}");
            Assert.Equal(0, prompts);
        }

        [Theory]
        [InlineData("IBM Plex Sans")]
        [InlineData("IBM Plex Serif")]
        [InlineData("IBM Plex Mono")]
        public async Task IbmPlex_families_resolve_from_the_monorepo_layout(string family)
        {
            var config = ShippedConfig();
            var resolver = new VendorRepoResolver(config, new HttpFontClient(), new FontArchiveInspector(), new FontLicenseEvaluator(config.Policy.License));

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(new FontFamilyNormalizer(config).Parse(family), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved, $"{family}: {result.FailureReason}");
            Assert.Equal("ibm-plex", result.SourceId);
        }

        [Fact]
        public async Task JetBrainsMono_resolves_unauthenticated_or_reports_the_rate_limit_clearly()
        {
            // Check the live API state first so the assertion matches reality:
            // OK → must resolve; rate-limited → must miss with the explicit
            // rate-limit reason; anything else (proxy/firewall block) → the API
            // is unreachable from here and there is nothing meaningful to assert.
            var http = new HttpFontClient();
            var apiState = await http.GetStringDetailedAsync(
                "https://api.github.com/repos/JetBrains/JetBrainsMono/releases/latest",
                new Dictionary<string, string> { ["Accept"] = "application/vnd.github+json" },
                TimeSpan.FromSeconds(30), CancellationToken.None);
            bool apiOk = apiState is { IsSuccess: true };
            bool apiRateLimited = apiState != null && VendorRepoResolver.IsGitHubRateLimited(apiState);
            if (!apiOk && !apiRateLimited) return;

            var config = ShippedConfig();
            var resolver = new VendorRepoResolver(config, http, new FontArchiveInspector(), new FontLicenseEvaluator(config.Policy.License),
                environment: _ => null);

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(new FontFamilyNormalizer(config).Parse("JetBrains Mono"), h.Context(), CancellationToken.None);

            if (apiOk)
            {
                Assert.True(result.IsResolved, $"JetBrains Mono: {result.FailureReason}");
            }
            else
            {
                Assert.False(result.IsResolved);
                Assert.Contains("rate limit", result.FailureReason, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public async Task JetBrainsMono_resolves_with_a_token_when_one_is_available()
        {
            string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (string.IsNullOrWhiteSpace(token)) return; // no token available — nothing to verify

            var config = ShippedConfig();
            var resolver = new VendorRepoResolver(config, new HttpFontClient(), new FontArchiveInspector(), new FontLicenseEvaluator(config.Policy.License));

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(new FontFamilyNormalizer(config).Parse("JetBrains Mono"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved, $"JetBrains Mono: {result.FailureReason}");
        }

        [Fact]
        public async Task Adobe_source_sans_pro_alias_still_resolves()
        {
            var config = ShippedConfig();
            var resolver = new VendorRepoResolver(config, new HttpFontClient(), new FontArchiveInspector(), new FontLicenseEvaluator(config.Policy.License));

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(new FontFamilyNormalizer(config).Parse("Source Sans Pro"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved, $"Source Sans Pro: {result.FailureReason}");
            Assert.Equal("adobe-source-sans", result.SourceId);
        }

        [Fact]
        public async Task DaFont_lemon_cookie_still_yields_a_candidate()
        {
            var config = ShippedConfig();
            var resolver = new CommunityFontResolver(config, new HttpFontClient(), new FontArchiveInspector(), new FontLicenseEvaluator(config.Policy.License));

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(new FontFamilyNormalizer(config).Parse("Lemon Cookie"), h.Context(), CancellationToken.None);

            // Community candidates are license-gated, so "found" may surface as
            // Installed or ManualReviewRequired — either proves the source works.
            Assert.NotEqual(AcquisitionStatus.Missing, result.Status);
        }
    }
}

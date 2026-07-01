using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FontSources;
using Xunit;

namespace PublisherConverter.Tests.FontSources
{
    public sealed class VendorRepoResolverTests
    {
        private const string Cfg = @"{
          ""policy"": { ""probeTimeoutMs"":3000, ""downloadTimeoutMs"":60000 },
          ""sources"": [
            { ""id"":""ibm-plex"", ""type"":""vendorRepo"", ""routingPatterns"":[""ibm"",""plex""],
              ""repo"":{""owner"":""IBM"",""repo"":""plex"",""branch"":""master""},
              ""pathTemplates"":[""packages/plex-sans/fonts/complete/ttf/{FamilyNoSpace}-{Style}.ttf""],
              ""styles"":[""Regular""], ""licenseHint"":""OFL"" },
            { ""id"":""jetbrains-mono"", ""type"":""vendorRepo"", ""routingPatterns"":[""jetbrains""],
              ""apiBaseUrl"":""https://api.github.com"",
              ""repo"":{""owner"":""JetBrains"",""repo"":""JetBrainsMono"",""releaseAssetPattern"":""JetBrainsMono-*.zip""},
              ""archive"":{ ""ttfPathHints"":[""fonts/ttf/""], ""licenseFileNames"":[""OFL.txt""] },
              ""licenseHint"":""OFL"" }
          ]
        }";

        private const string JetBrainsApiUrl = "https://api.github.com/repos/JetBrains/JetBrainsMono/releases/latest";

        private static (VendorRepoResolver resolver, FakeFontHttpClient http, FontFamilyNormalizer norm) Build(
            string? configJson = null, Func<string, string?>? environment = null)
        {
            var config = FontSourceConfiguration.LoadFromJson(configJson ?? Cfg);
            var http = new FakeFontHttpClient();
            // Default to an empty environment so a real GITHUB_TOKEN on the test
            // machine can't leak into assertions.
            var resolver = new VendorRepoResolver(config, http, new FontArchiveInspector(), new FontLicenseEvaluator(config.Policy.License),
                environment: environment ?? (_ => null));
            return (resolver, http, new FontFamilyNormalizer(config));
        }

        private static void SeedJetBrainsRelease(FakeFontHttpClient http)
        {
            http.SeedPage(JetBrainsApiUrl,
                @"{ ""assets"": [ { ""name"":""JetBrainsMono-2.304.zip"", ""browser_download_url"":""https://dl/jb.zip"" } ] }");
            byte[] zip = ZipBuilder.Build(new[]
            {
                ("fonts/ttf/JetBrainsMono-Regular.ttf", TtfTestBuilder.BuildValidTtf("JetBrains Mono")),
                ("OFL.txt", ZipBuilder.Text("SIL Open Font License")),
            });
            http.SeedFile("https://dl/jb.zip", zip);
        }

        [Fact]
        public async Task Resolves_via_raw_path_for_routed_family()
        {
            var (resolver, http, norm) = Build();
            http.SeedFile("https://raw.githubusercontent.com/IBM/plex/master/packages/plex-sans/fonts/complete/ttf/IBMPlexSans-Regular.ttf",
                TtfTestBuilder.BuildValidTtf("IBM Plex Sans"));

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("IBM Plex Sans"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved);
            Assert.Equal("ibm-plex", result.SourceId);
            Assert.Single(h.Installer.InstalledFromStream);
        }

        [Fact]
        public async Task Unrouted_family_misses_without_probing_anything()
        {
            var (resolver, http, norm) = Build();
            using var h = new ResolverHarness();

            var result = await resolver.TryResolveAsync(norm.Parse("Arial"), h.Context(), CancellationToken.None);

            Assert.False(result.IsResolved);
            Assert.Empty(http.Probed);     // no unrelated repository was probed
            Assert.Empty(http.Downloaded);
        }

        [Fact]
        public async Task Resolves_from_release_archive_extracting_only_ttf()
        {
            var (resolver, http, norm) = Build();

            http.SeedPage("https://api.github.com/repos/JetBrains/JetBrainsMono/releases/latest",
                @"{ ""assets"": [ { ""name"":""JetBrainsMono-2.304.zip"", ""browser_download_url"":""https://dl/jb.zip"" } ] }");

            byte[] zip = ZipBuilder.Build(new[]
            {
                ("fonts/ttf/JetBrainsMono-Regular.ttf", TtfTestBuilder.BuildValidTtf("JetBrains Mono")),
                ("preview.png", new byte[] { 1, 2, 3 }),
                ("OFL.txt", ZipBuilder.Text("SIL Open Font License")),
            });
            http.SeedFile("https://dl/jb.zip", zip);

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("JetBrains Mono"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved);
            Assert.Equal("jetbrains-mono", result.SourceId);
            Assert.Single(h.Installer.InstalledFromStream);
            Assert.Equal("JetBrains Mono", h.Installer.InstalledFromStream[0].family);
        }

        [Fact]
        public async Task Api_call_carries_bearer_header_when_token_is_configured()
        {
            string cfgWithToken = Cfg.Replace(
                @"""policy"": { ""probeTimeoutMs"":3000, ""downloadTimeoutMs"":60000 }",
                @"""policy"": { ""probeTimeoutMs"":3000, ""downloadTimeoutMs"":60000, ""gitHubToken"":""cfg-token"" }");
            var (resolver, http, norm) = Build(cfgWithToken);
            SeedJetBrainsRelease(http);

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("JetBrains Mono"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved);
            var apiRequest = http.FetchedRequests.Single(r => r.Url == JetBrainsApiUrl);
            Assert.NotNull(apiRequest.Headers);
            Assert.Equal("Bearer cfg-token", apiRequest.Headers!["Authorization"]);
        }

        [Fact]
        public async Task Api_call_carries_bearer_header_from_GITHUB_TOKEN_environment()
        {
            var (resolver, http, norm) = Build(environment: name => name == "GITHUB_TOKEN" ? "env-token" : null);
            SeedJetBrainsRelease(http);

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("JetBrains Mono"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved);
            var apiRequest = http.FetchedRequests.Single(r => r.Url == JetBrainsApiUrl);
            Assert.Equal("Bearer env-token", apiRequest.Headers!["Authorization"]);
        }

        [Fact]
        public async Task Api_call_has_no_auth_header_and_still_resolves_when_no_token_present()
        {
            var (resolver, http, norm) = Build();
            SeedJetBrainsRelease(http);

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("JetBrains Mono"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved);
            var apiRequest = http.FetchedRequests.Single(r => r.Url == JetBrainsApiUrl);
            Assert.NotNull(apiRequest.Headers);
            Assert.False(apiRequest.Headers!.ContainsKey("Authorization"));
        }

        [Fact]
        public async Task Rate_limited_403_yields_explicit_rate_limit_miss()
        {
            var (resolver, http, norm) = Build();
            http.SeedResponse(JetBrainsApiUrl, 403,
                @"{ ""message"": ""API rate limit exceeded for 203.0.113.7. (But here's the good news: Authenticated requests get a higher rate limit.)"" }",
                new Dictionary<string, string> { ["X-RateLimit-Remaining"] = "0" });

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("JetBrains Mono"), h.Context(), CancellationToken.None);

            Assert.False(result.IsResolved);
            Assert.Equal(AcquisitionStatus.Missing, result.Status);
            Assert.Contains("rate limit", result.FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("GITHUB_TOKEN", result.FailureReason); // hints the remedy
            Assert.Empty(http.Downloaded); // no asset fetch was attempted
        }

        [Fact]
        public async Task Non_rate_limit_403_keeps_the_generic_metadata_reason()
        {
            var (resolver, http, norm) = Build();
            http.SeedResponse(JetBrainsApiUrl, 403, @"{ ""message"": ""Repository access blocked"" }");

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("JetBrains Mono"), h.Context(), CancellationToken.None);

            Assert.False(result.IsResolved);
            Assert.Equal("release metadata unavailable", result.FailureReason);
        }

        [Fact]
        public async Task Release_asset_url_is_cached_so_repeat_requests_skip_the_api()
        {
            var (resolver, http, norm) = Build();
            SeedJetBrainsRelease(http);

            using var h = new ResolverHarness();
            var first = await resolver.TryResolveAsync(norm.Parse("JetBrains Mono"), h.Context(), CancellationToken.None);
            var second = await resolver.TryResolveAsync(norm.Parse("JetBrains Mono Bold"), h.Context(), CancellationToken.None);

            Assert.True(first.IsResolved);
            Assert.True(second.IsResolved);
            Assert.Single(http.FetchedRequests.Where(r => r.Url == JetBrainsApiUrl));
        }

        [Fact]
        public void IsGitHubRateLimited_distinguishes_rate_limit_responses()
        {
            Assert.True(VendorRepoResolver.IsGitHubRateLimited(new TextFetchResult
            {
                StatusCode = 403,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-RateLimit-Remaining"] = "0" },
            }));
            Assert.True(VendorRepoResolver.IsGitHubRateLimited(new TextFetchResult
            {
                StatusCode = 403,
                Body = @"{""message"":""API rate limit exceeded for 1.2.3.4.""}",
            }));
            Assert.False(VendorRepoResolver.IsGitHubRateLimited(new TextFetchResult { StatusCode = 403, Body = "forbidden" }));
            Assert.False(VendorRepoResolver.IsGitHubRateLimited(new TextFetchResult
            {
                StatusCode = 200,
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-RateLimit-Remaining"] = "0" },
            }));
        }

        [Fact]
        public void SelectAsset_matches_glob_pattern()
        {
            string json = @"{ ""assets"": [ { ""name"":""other.txt"", ""browser_download_url"":""u1"" }, { ""name"":""JetBrainsMono-1.zip"", ""browser_download_url"":""u2"" } ] }";
            Assert.Equal("u2", VendorRepoResolver.SelectAsset(json, "JetBrainsMono-*.zip"));
            Assert.Null(VendorRepoResolver.SelectAsset(json, "NoSuch-*.zip"));
        }
    }
}

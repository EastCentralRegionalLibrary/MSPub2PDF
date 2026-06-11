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
              ""pathTemplates"":[""IBM-Plex-Sans/fonts/complete/ttf/{FamilyNoSpace}-{Style}.ttf""],
              ""styles"":[""Regular""], ""licenseHint"":""OFL"" },
            { ""id"":""jetbrains-mono"", ""type"":""vendorRepo"", ""routingPatterns"":[""jetbrains""],
              ""apiBaseUrl"":""https://api.github.com"",
              ""repo"":{""owner"":""JetBrains"",""repo"":""JetBrainsMono"",""releaseAssetPattern"":""JetBrainsMono-*.zip""},
              ""archive"":{ ""ttfPathHints"":[""fonts/ttf/""], ""licenseFileNames"":[""OFL.txt""] },
              ""licenseHint"":""OFL"" }
          ]
        }";

        private static (VendorRepoResolver resolver, FakeFontHttpClient http, FontFamilyNormalizer norm) Build()
        {
            var config = FontSourceConfiguration.LoadFromJson(Cfg);
            var http = new FakeFontHttpClient();
            var resolver = new VendorRepoResolver(config, http, new FontArchiveInspector(), new FontLicenseEvaluator(config.Policy.License));
            return (resolver, http, new FontFamilyNormalizer(config));
        }

        [Fact]
        public async Task Resolves_via_raw_path_for_routed_family()
        {
            var (resolver, http, norm) = Build();
            http.SeedFile("https://raw.githubusercontent.com/IBM/plex/master/IBM-Plex-Sans/fonts/complete/ttf/IBMPlexSans-Regular.ttf",
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
        public void SelectAsset_matches_glob_pattern()
        {
            string json = @"{ ""assets"": [ { ""name"":""other.txt"", ""browser_download_url"":""u1"" }, { ""name"":""JetBrainsMono-1.zip"", ""browser_download_url"":""u2"" } ] }";
            Assert.Equal("u2", VendorRepoResolver.SelectAsset(json, "JetBrainsMono-*.zip"));
            Assert.Null(VendorRepoResolver.SelectAsset(json, "NoSuch-*.zip"));
        }
    }
}

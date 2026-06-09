using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FontSources;
using Xunit;

namespace PublisherConverter.Tests.FontSources
{
    public sealed class CommunityFontResolverTests
    {
        private const string Cfg = @"{
          ""policy"": { ""probeTimeoutMs"":3000, ""downloadTimeoutMs"":60000, ""communityEnabled"":true,
            ""license"": { ""unknownLicenseAction"":""manualReviewRequired"" } },
          ""sources"": [ { ""id"":""dafont"", ""type"":""community"",
            ""baseUrl"":""https://www.dafont.com"",
            ""slugTemplate"":""https://dl.dafont.com/dl/?f={slug}"",
            ""searchTemplate"":""https://www.dafont.com/search.php?q={query}"",
            ""probeStrategy"":""slugThenSearch"",
            ""archive"":{ ""extractExtensions"":["".ttf""], ""licenseFileNames"":[""readme.txt""] } } ]
        }";

        private static (CommunityFontResolver resolver, FakeFontHttpClient http, FontFamilyNormalizer norm) Build()
        {
            var config = FontSourceConfiguration.LoadFromJson(Cfg);
            var http = new FakeFontHttpClient();
            var resolver = new CommunityFontResolver(config, http, new FontArchiveInspector(), new FontLicenseEvaluator(config.Policy.License));
            return (resolver, http, new FontFamilyNormalizer(config));
        }

        [Fact]
        public async Task Direct_slug_with_personal_use_license_requires_manual_review()
        {
            var (resolver, http, norm) = Build();
            byte[] zip = ZipBuilder.Build(new[]
            {
                ("CoolFont.ttf", TtfTestBuilder.BuildValidTtf("Cool Font")),
                ("readme.txt", ZipBuilder.Text("Cool Font is free for personal use only.")),
            });
            http.SeedFile("https://dl.dafont.com/dl/?f=coolfont", zip);

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Cool Font"), h.Context(), CancellationToken.None);

            Assert.Equal(AcquisitionStatus.ManualReviewRequired, result.Status);
            Assert.True(result.ManualReviewRequired);
            Assert.Empty(h.Installer.InstalledFromStream); // NOT auto-installed
        }

        [Fact]
        public async Task Direct_slug_with_ofl_license_installs()
        {
            var (resolver, http, norm) = Build();
            byte[] zip = ZipBuilder.Build(new[]
            {
                ("CoolFont.ttf", TtfTestBuilder.BuildValidTtf("Cool Font")),
                ("readme.txt", ZipBuilder.Text("SIL Open Font License (OFL)")),
            });
            http.SeedFile("https://dl.dafont.com/dl/?f=coolfont", zip);

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Cool Font"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved);
            Assert.Equal(LicenseStatus.Allowed, result.License);
            Assert.Single(h.Installer.InstalledFromStream);
        }

        [Fact]
        public async Task Search_fallback_used_when_slug_missing()
        {
            var (resolver, http, norm) = Build();
            // No direct slug; search page points to the real slug.
            http.SeedPage("https://www.dafont.com/search.php?q=Cool%20Font",
                @"<a href=""font.php?f=coolfont"">Cool Font</a>");
            byte[] zip = ZipBuilder.Build(new[]
            {
                ("CoolFont.ttf", TtfTestBuilder.BuildValidTtf("Cool Font")),
                ("readme.txt", ZipBuilder.Text("OFL")),
            });
            http.SeedFile("https://dl.dafont.com/dl/?f=coolfont", zip);

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Cool Font"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved);
        }

        [Fact]
        public async Task Search_below_confidence_threshold_is_a_miss()
        {
            var (resolver, http, norm) = Build();
            http.SeedPage("https://www.dafont.com/search.php?q=Cool%20Font",
                @"<a href=""font.php?f=somethingcompletelyunrelated"">x</a>");

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Cool Font"), h.Context(), CancellationToken.None);

            Assert.False(result.IsResolved);
            Assert.Empty(http.Downloaded);
        }

        [Fact]
        public void BestSearchMatch_prefers_closest_slug()
        {
            string html = @"<a href=""?f=coolfontpro"">a</a> <a href=""?f=coolfont"">b</a> <a href=""?f=zzz"">c</a>";
            var (slug, confidence) = CommunityFontResolver.BestSearchMatch(html, "coolfont");
            Assert.Equal("coolfont", slug);
            Assert.Equal(1.0, confidence, 3);
        }

        [Fact]
        public void Disabled_when_community_policy_off()
        {
            string json = Cfg.Replace("\"communityEnabled\":true", "\"communityEnabled\":false");
            var config = FontSourceConfiguration.LoadFromJson(json);
            var resolver = new CommunityFontResolver(config, new FakeFontHttpClient(), new FontArchiveInspector(), new FontLicenseEvaluator(config.Policy.License));
            Assert.False(resolver.IsEnabled);
        }
    }
}

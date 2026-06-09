using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FontSources;
using Xunit;

namespace PublisherConverter.Tests.FontSources
{
    public sealed class GoogleFontsResolverTests
    {
        private const string Cfg = @"{
          ""policy"": { ""probeTimeoutMs"":3000, ""downloadTimeoutMs"":60000 },
          ""sources"": [ { ""id"":""google-fonts"", ""type"":""googleFonts"",
            ""repo"":{""owner"":""google"",""repo"":""fonts"",""branch"":""main""},
            ""licenseDirs"":[""ofl""],
            ""pathTemplates"":[""{licenseDir}/{slug}/{FamilyNoSpace}-{Style}.ttf""],
            ""styles"":[""Regular"",""Bold""], ""licenseHint"":""OFL"" } ]
        }";

        private const string Base = "https://raw.githubusercontent.com/google/fonts/main/ofl/opensans/";

        private static (GoogleFontsResolver resolver, FakeFontHttpClient http, FontFamilyNormalizer norm) Build()
        {
            var config = FontSourceConfiguration.LoadFromJson(Cfg);
            var http = new FakeFontHttpClient();
            var resolver = new GoogleFontsResolver(config, http, new FontLicenseEvaluator(config.Policy.License));
            return (resolver, http, new FontFamilyNormalizer(config));
        }

        [Fact]
        public async Task Resolves_regular_and_bold_static_ttf()
        {
            var (resolver, http, norm) = Build();
            http.SeedFile(Base + "OpenSans-Regular.ttf", TtfTestBuilder.BuildValidTtf("Open Sans"));
            http.SeedFile(Base + "OpenSans-Bold.ttf", TtfTestBuilder.BuildValidTtf("Open Sans"));

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Open Sans"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved);
            Assert.Equal(ResolutionLayer.GoogleFonts, result.Layer);
            Assert.Equal(LicenseStatus.NotApplicable, result.License);
            Assert.Equal(2, h.Installer.InstalledFromStream.Count); // Regular + Bold
        }

        [Fact]
        public async Task Misses_unknown_family_without_downloading()
        {
            var (resolver, http, norm) = Build();
            using var h = new ResolverHarness();

            var result = await resolver.TryResolveAsync(norm.Parse("Totally Unknown"), h.Context(), CancellationToken.None);

            Assert.False(result.IsResolved);
            Assert.Equal(AcquisitionStatus.Missing, result.Status);
            Assert.Empty(http.Downloaded);
            Assert.Empty(h.Installer.InstalledFromStream);
        }

        [Fact]
        public async Task Rejects_non_ttf_payload_served_under_ttf_url()
        {
            var (resolver, http, norm) = Build();
            // OTTO (OpenType/CFF) bytes masquerading at the .ttf URL.
            http.SeedFile(Base + "OpenSans-Regular.ttf", new byte[] { 0x4F, 0x54, 0x54, 0x4F, 0, 1, 0, 0, 0, 0, 0, 0 });

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Open Sans"), h.Context(), CancellationToken.None);

            Assert.False(result.IsResolved);
            Assert.Empty(h.Installer.InstalledFromStream);
        }
    }
}

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core;
using Xunit;

namespace PublisherConverter.Tests
{
    public sealed class GoogleFontsStrategyTests
    {
        // ---- helper-method tests (cross-platform, no Win32 needed) ----

        [Fact]
        public void ExtractSrcUrls_pulls_quoted_and_unquoted_urls()
        {
            const string css = @"
                @font-face { src: url('https://fonts.gstatic.com/s/a/1.ttf') format('truetype'); }
                @font-face { src: url(""https://fonts.gstatic.com/s/b/2.woff2"") format('woff2'); }
                @font-face { src: url(https://fonts.gstatic.com/s/c/3.otf) format('opentype'); }
            ";

            var urls = GoogleFontsProvisioningStrategy.ExtractSrcUrls(css);

            Assert.Equal(new[]
            {
                "https://fonts.gstatic.com/s/a/1.ttf",
                "https://fonts.gstatic.com/s/b/2.woff2",
                "https://fonts.gstatic.com/s/c/3.otf",
            }, urls);
        }

        [Fact]
        public void ExtractSrcUrls_dedupes_repeated_urls()
        {
            const string css = @"
                @font-face { src: url('https://fonts.gstatic.com/x/x.ttf'); }
                @font-face { src: url('https://fonts.gstatic.com/x/x.ttf'); }
            ";

            Assert.Single(GoogleFontsProvisioningStrategy.ExtractSrcUrls(css));
        }

        [Fact]
        public void IsInstallableFontExtension_only_accepts_ttf_and_otf()
        {
            Assert.True(GoogleFontsProvisioningStrategy.IsInstallableFontExtension(".ttf"));
            Assert.True(GoogleFontsProvisioningStrategy.IsInstallableFontExtension(".otf"));
            Assert.False(GoogleFontsProvisioningStrategy.IsInstallableFontExtension(".woff2"));
            Assert.False(GoogleFontsProvisioningStrategy.IsInstallableFontExtension(".woff"));
            Assert.False(GoogleFontsProvisioningStrategy.IsInstallableFontExtension(""));
        }

        [Fact]
        public void ExtractFileExtension_handles_query_strings()
        {
            Assert.Equal(".ttf", GoogleFontsProvisioningStrategy.ExtractFileExtension("https://fonts.gstatic.com/x/y.ttf?v=1"));
            Assert.Equal(".woff2", GoogleFontsProvisioningStrategy.ExtractFileExtension("https://fonts.gstatic.com/x/y.woff2"));
        }

        // ---- end-to-end strategy with fakes ----

        private const string SampleCssWithTtf = @"
            @font-face { font-family: 'Test'; src: url('https://fonts.gstatic.com/s/test/Test-Regular.ttf') format('truetype'); }
        ";

        private const string SampleCssWoff2Only = @"
            @font-face { font-family: 'Test'; src: url('https://fonts.gstatic.com/s/test/Test-Regular.woff2') format('woff2'); }
        ";

        private static FontMappingTable Mapping(string family = "Roboto")
        {
            string json = $@"{{ ""googleFonts"": {{ ""{family}"": {{}} }} }}";
            return FontMappingLoader.LoadFromJson(json);
        }

        [Fact]
        public async Task Installs_ttf_variant_returned_from_css_endpoint()
        {
            var downloader = new FakeFontDownloader();
            downloader.RespondWithString(
                "https://fonts.googleapis.com/css?family=Roboto",
                SampleCssWithTtf);

            var installer = new FakeUserFontInstaller();
            var strategy = new GoogleFontsProvisioningStrategy(Mapping(), downloader, installer);

            var log = new List<string>();
            bool ok = await strategy.TryResolveAsync("Roboto", log, CancellationToken.None);

            Assert.True(ok);
            Assert.Single(installer.InstalledFromUrl);
            Assert.Equal("Roboto", installer.InstalledFromUrl[0].family);
            Assert.Equal("https://fonts.gstatic.com/s/test/Test-Regular.ttf", installer.InstalledFromUrl[0].url);
            // Legacy UA header was sent.
            Assert.NotNull(downloader.RequestedHeaders[0]);
            Assert.Contains(downloader.RequestedHeaders[0]!, h =>
                h.Key.Equals("User-Agent", System.StringComparison.OrdinalIgnoreCase)
                && h.Value.Contains("MSIE"));
        }

        [Fact]
        public async Task Skips_woff2_only_responses_and_returns_false()
        {
            var downloader = new FakeFontDownloader();
            downloader.RespondWithString(
                "https://fonts.googleapis.com/css?family=Roboto",
                SampleCssWoff2Only);

            var installer = new FakeUserFontInstaller();
            var strategy = new GoogleFontsProvisioningStrategy(Mapping(), downloader, installer);

            var log = new List<string>();
            bool ok = await strategy.TryResolveAsync("Roboto", log, CancellationToken.None);

            Assert.False(ok);
            Assert.Empty(installer.InstalledFromUrl);
            Assert.Contains(log, l => l.Contains("non-installable") || l.Contains("no TTF/OTF"));
        }

        [Fact]
        public async Task Network_failure_returns_false_without_throwing()
        {
            var downloader = new FakeFontDownloader(); // no response seeded → HttpRequestException
            var installer = new FakeUserFontInstaller();
            var strategy = new GoogleFontsProvisioningStrategy(Mapping(), downloader, installer);

            var log = new List<string>();
            bool ok = await strategy.TryResolveAsync("Roboto", log, CancellationToken.None);

            Assert.False(ok);
            Assert.Contains(log, l => l.Contains("CSS fetch failed"));
        }

        [Fact]
        public async Task CanResolve_returns_false_for_unmapped_family()
        {
            var strategy = new GoogleFontsProvisioningStrategy(Mapping("Roboto"), new FakeFontDownloader(), new FakeUserFontInstaller());
            Assert.False(strategy.CanResolve("Unknown Font"));
            // And TryResolveAsync should be a clean no-op.
            var log = new List<string>();
            Assert.False(await strategy.TryResolveAsync("Unknown Font", log, CancellationToken.None));
        }
    }
}

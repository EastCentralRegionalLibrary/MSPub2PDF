using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core;
using Xunit;

namespace PublisherConverter.Tests
{
    public sealed class GitHubFontsStrategyTests
    {
        // ---- helper-method tests (cross-platform) ----

        [Fact]
        public void GlobToRegex_matches_star_and_question()
        {
            var rx = GitHubFontsProvisioningStrategy.GlobToRegex("Inter-*.zip");
            Assert.Matches(rx, "Inter-3.19.zip");
            Assert.DoesNotMatch(rx, "Inter.tar.gz");
            Assert.DoesNotMatch(rx, "Other-3.19.zip");
        }

        [Fact]
        public void IsFontFile_recognises_ttf_and_otf_only()
        {
            Assert.True(GitHubFontsProvisioningStrategy.IsFontFile("Foo.ttf"));
            Assert.True(GitHubFontsProvisioningStrategy.IsFontFile("Foo.OTF"));
            Assert.False(GitHubFontsProvisioningStrategy.IsFontFile("Foo.zip"));
            Assert.False(GitHubFontsProvisioningStrategy.IsFontFile("Foo.woff2"));
        }

        [Fact]
        public void FilterAssets_supports_exact_pattern_and_default_modes()
        {
            var assets = new[]
            {
                new GitHubFontsProvisioningStrategy.ReleaseAsset { Name = "Inter-3.19.zip",  BrowserDownloadUrl = "u1" },
                new GitHubFontsProvisioningStrategy.ReleaseAsset { Name = "README.md",       BrowserDownloadUrl = "u2" },
                new GitHubFontsProvisioningStrategy.ReleaseAsset { Name = "Inter-Bold.ttf",  BrowserDownloadUrl = "u3" },
                new GitHubFontsProvisioningStrategy.ReleaseAsset { Name = "Inter-Italic.otf",BrowserDownloadUrl = "u4" },
            };

            // exact name
            var exact = GitHubFontsProvisioningStrategy.FilterAssets(assets, "Inter-Bold.ttf", null);
            Assert.Single(exact);
            Assert.Equal("u3", exact[0].BrowserDownloadUrl);

            // pattern
            var pattern = GitHubFontsProvisioningStrategy.FilterAssets(assets, null, "Inter-*.zip");
            Assert.Single(pattern);
            Assert.Equal("u1", pattern[0].BrowserDownloadUrl);

            // no filter — fonts + archives only (skip README)
            var defaults = GitHubFontsProvisioningStrategy.FilterAssets(assets, null, null);
            Assert.Equal(3, defaults.Count);
            Assert.DoesNotContain(defaults, a => a.Name == "README.md");
        }

        // ---- raw-content mode end-to-end ----

        [Fact]
        public async Task Raw_content_path_downloads_and_installs_single_font()
        {
            string json = @"{ ""githubFonts"": { ""Roboto"": {
                ""owner"":""google"", ""repo"":""fonts"", ""ref"":""main"",
                ""path"":""apache/roboto/Roboto-Regular.ttf""
            } } }";
            var table = FontMappingLoader.LoadFromJson(json);

            var downloader = new FakeFontDownloader();
            var installer = new FakeUserFontInstaller();
            var strategy = new GitHubFontsProvisioningStrategy(table, downloader, installer);

            var log = new List<string>();
            bool ok = await strategy.TryResolveAsync("Roboto", log, CancellationToken.None);

            Assert.True(ok);
            Assert.Single(installer.InstalledFromUrl);
            Assert.Equal("https://raw.githubusercontent.com/google/fonts/main/apache/roboto/Roboto-Regular.ttf",
                installer.InstalledFromUrl[0].url);
            Assert.Equal("Roboto-Regular.ttf", installer.InstalledFromUrl[0].fileName);
        }

        // ---- release-asset mode end-to-end ----

        private const string LatestReleaseJsonWithZip = @"{
            ""tag_name"": ""v3.19"",
            ""assets"": [
                { ""name"": ""Inter-3.19.zip"",  ""browser_download_url"": ""https://example/inter-zip"" },
                { ""name"": ""LICENSE"",         ""browser_download_url"": ""https://example/license"" }
            ]
        }";

        private const string LatestReleaseJsonWithTtf = @"{
            ""tag_name"": ""v1.0"",
            ""assets"": [
                { ""name"": ""Inter-Regular.ttf"", ""browser_download_url"": ""https://example/inter-ttf"" }
            ]
        }";

        [Fact]
        public async Task Release_mode_filters_by_pattern_then_installs()
        {
            string json = @"{ ""githubFonts"": { ""Inter"": {
                ""owner"":""rsms"", ""repo"":""inter"",
                ""assetPattern"": ""Inter-*.ttf""
            } } }";
            var table = FontMappingLoader.LoadFromJson(json);

            var downloader = new FakeFontDownloader();
            downloader.RespondWithString(
                "https://api.github.com/repos/rsms/inter/releases/latest",
                LatestReleaseJsonWithTtf);

            var installer = new FakeUserFontInstaller();
            var strategy = new GitHubFontsProvisioningStrategy(table, downloader, installer);

            var log = new List<string>();
            bool ok = await strategy.TryResolveAsync("Inter", log, CancellationToken.None);

            Assert.True(ok);
            Assert.Single(installer.InstalledFromUrl);
            Assert.Equal("https://example/inter-ttf", installer.InstalledFromUrl[0].url);

            // The api request advertised a JSON Accept header.
            Assert.NotNull(downloader.RequestedHeaders[0]);
            Assert.Contains(downloader.RequestedHeaders[0]!, h => h.Key == "Accept" && h.Value.Contains("vnd.github"));
        }

        [Fact]
        public async Task Release_mode_extracts_zip_and_installs_each_font_entry()
        {
            string json = @"{ ""githubFonts"": { ""Inter"": {
                ""owner"":""rsms"", ""repo"":""inter"",
                ""assetPattern"": ""Inter-*.zip""
            } } }";
            var table = FontMappingLoader.LoadFromJson(json);

            var downloader = new FakeFontDownloader();
            downloader.RespondWithString(
                "https://api.github.com/repos/rsms/inter/releases/latest",
                LatestReleaseJsonWithZip);
            downloader.RespondWithBytes("https://example/inter-zip", BuildZipWith(new[]
            {
                ("Inter-Regular.ttf",   new byte[] { 1, 2, 3, 4 }),
                ("Inter-Bold.otf",      new byte[] { 5, 6, 7, 8 }),
                ("readme.txt",          Encoding.UTF8.GetBytes("not a font")),
            }));

            var installer = new FakeUserFontInstaller();
            var strategy = new GitHubFontsProvisioningStrategy(table, downloader, installer);

            var log = new List<string>();
            bool ok = await strategy.TryResolveAsync("Inter", log, CancellationToken.None);

            Assert.True(ok);
            Assert.Equal(2, installer.InstalledFromStream.Count);
            Assert.Contains(installer.InstalledFromStream, x => x.fileName == "Inter-Regular.ttf");
            Assert.Contains(installer.InstalledFromStream, x => x.fileName == "Inter-Bold.otf");
            Assert.DoesNotContain(installer.InstalledFromStream, x => x.fileName == "readme.txt");
        }

        [Fact]
        public async Task Network_failure_in_release_fetch_returns_false()
        {
            string json = @"{ ""githubFonts"": { ""Inter"": { ""owner"":""rsms"", ""repo"":""inter"" } } }";
            var table = FontMappingLoader.LoadFromJson(json);

            var downloader = new FakeFontDownloader(); // no response → throws
            var installer = new FakeUserFontInstaller();
            var strategy = new GitHubFontsProvisioningStrategy(table, downloader, installer);

            var log = new List<string>();
            bool ok = await strategy.TryResolveAsync("Inter", log, CancellationToken.None);

            Assert.False(ok);
            Assert.Empty(installer.InstalledFromUrl);
            Assert.Contains(log, l => l.Contains("release fetch failed"));
        }

        [Fact]
        public async Task Malformed_zip_archive_returns_false_without_throwing()
        {
            string json = @"{ ""githubFonts"": { ""Inter"": {
                ""owner"":""rsms"", ""repo"":""inter"",
                ""assetPattern"": ""Inter-*.zip""
            } } }";
            var table = FontMappingLoader.LoadFromJson(json);

            var downloader = new FakeFontDownloader();
            downloader.RespondWithString(
                "https://api.github.com/repos/rsms/inter/releases/latest",
                LatestReleaseJsonWithZip);
            downloader.RespondWithBytes("https://example/inter-zip", Encoding.UTF8.GetBytes("not a real zip"));

            var installer = new FakeUserFontInstaller();
            var strategy = new GitHubFontsProvisioningStrategy(table, downloader, installer);

            var log = new List<string>();
            bool ok = await strategy.TryResolveAsync("Inter", log, CancellationToken.None);

            Assert.False(ok);
            Assert.Empty(installer.InstalledFromStream);
            Assert.Contains(log, l => l.Contains("archive") && (l.Contains("not a valid ZIP") || l.Contains("extraction failed")));
        }

        [Fact]
        public async Task Filter_with_no_matches_logs_available_assets()
        {
            string json = @"{ ""githubFonts"": { ""Inter"": {
                ""owner"":""rsms"", ""repo"":""inter"",
                ""assetPattern"": ""*.tar.gz""
            } } }";
            var table = FontMappingLoader.LoadFromJson(json);

            var downloader = new FakeFontDownloader();
            downloader.RespondWithString(
                "https://api.github.com/repos/rsms/inter/releases/latest",
                LatestReleaseJsonWithTtf);

            var installer = new FakeUserFontInstaller();
            var strategy = new GitHubFontsProvisioningStrategy(table, downloader, installer);

            var log = new List<string>();
            bool ok = await strategy.TryResolveAsync("Inter", log, CancellationToken.None);

            Assert.False(ok);
            Assert.Contains(log, l => l.Contains("no assets matched") && l.Contains("Inter-Regular.ttf"));
        }

        // ---- helpers ----

        private static byte[] BuildZipWith(IEnumerable<(string name, byte[] content)> entries)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, content) in entries)
                {
                    var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                    using var s = entry.Open();
                    s.Write(content, 0, content.Length);
                }
            }
            return ms.ToArray();
        }
    }
}

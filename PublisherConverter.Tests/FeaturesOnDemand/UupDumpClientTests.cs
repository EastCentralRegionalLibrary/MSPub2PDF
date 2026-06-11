using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FeaturesOnDemand;
using Xunit;

namespace PublisherConverter.Tests.FeaturesOnDemand
{
    public sealed class UupDumpClientTests
    {
        private const string Base = "https://api.uupdump.net";
        private const string ListUrl = Base + "/listid.php?search=26100&sortByDate=1";

        private static UupDumpClient NewClient(FakeFontDownloader downloader) => new UupDumpClient(downloader);

        // ---- listid.php ----

        [Fact]
        public async Task FindLatestUpdateId_picks_matching_architecture()
        {
            var dl = new FakeFontDownloader();
            dl.RespondWithString(ListUrl, @"{ ""response"": { ""builds"": [
                { ""title"":""arm"", ""arch"":""arm64"", ""uuid"":""uuid-arm"" },
                { ""title"":""amd"", ""arch"":""amd64"", ""uuid"":""uuid-amd"" }
            ]}}");

            string id = await NewClient(dl).FindLatestUpdateIdAsync("26100", "amd64", null, CancellationToken.None);

            Assert.Equal("uuid-amd", id);
        }

        [Fact]
        public async Task FindLatestUpdateId_handles_builds_as_object()
        {
            var dl = new FakeFontDownloader();
            dl.RespondWithString(ListUrl, @"{ ""response"": { ""builds"": {
                ""0"": { ""arch"":""arm64"", ""uuid"":""uuid-arm"" },
                ""1"": { ""arch"":""amd64"", ""uuid"":""uuid-amd"" }
            }}}");

            string id = await NewClient(dl).FindLatestUpdateIdAsync("26100", "amd64", null, CancellationToken.None);

            Assert.Equal("uuid-amd", id);
        }

        [Fact]
        public async Task FindLatestUpdateId_throws_when_no_matching_arch()
        {
            var dl = new FakeFontDownloader();
            dl.RespondWithString(ListUrl, @"{ ""response"": { ""builds"": [
                { ""arch"":""arm64"", ""uuid"":""uuid-arm"" }
            ]}}");

            await Assert.ThrowsAsync<UupDumpException>(() =>
                NewClient(dl).FindLatestUpdateIdAsync("26100", "amd64", null, CancellationToken.None));
        }

        [Fact]
        public async Task FindLatestUpdateId_throws_on_network_error()
        {
            var dl = new FakeFontDownloader(); // nothing seeded → throws
            await Assert.ThrowsAsync<UupDumpException>(() =>
                NewClient(dl).FindLatestUpdateIdAsync("26100", "amd64", null, CancellationToken.None));
        }

        // ---- get.php ----

        [Fact]
        public async Task GetFiles_parses_manifest_with_numeric_and_string_sizes()
        {
            var dl = new FakeFontDownloader();
            dl.RespondWithString(Base + "/get.php?id=u1", @"{ ""response"": { ""files"": {
                ""a.cab"": { ""size"": 1234, ""url"":""http://x/a"" },
                ""b.cab"": { ""size"": ""5678"", ""downloadUrl"":""http://x/b"" }
            }}}");

            var files = await NewClient(dl).GetFilesAsync("u1", null, CancellationToken.None);

            Assert.Equal(2, files.Count);
            Assert.Equal(1234, files["a.cab"].Size);
            Assert.Equal(5678, files["b.cab"].Size);
            Assert.Equal("http://x/a", files["a.cab"].Url);
            Assert.Equal("http://x/b", files["b.cab"].Url); // downloadUrl fallback
        }

        [Fact]
        public async Task GetFiles_throws_on_empty_manifest()
        {
            var dl = new FakeFontDownloader();
            dl.RespondWithString(Base + "/get.php?id=u1", @"{ ""response"": { ""files"": {} } }");

            await Assert.ThrowsAsync<UupDumpException>(() =>
                NewClient(dl).GetFilesAsync("u1", null, CancellationToken.None));
        }

        // ---- package selection ----

        private static IReadOnlyDictionary<string, UupFile> Manifest(params UupFile[] files)
        {
            var d = new Dictionary<string, UupFile>();
            foreach (var f in files) d[f.FileName] = f;
            return d;
        }

        private static UupFile F(string name, long size, string? url = "http://x/cab")
            => new UupFile { FileName = name, Size = size, Url = url };

        [Fact]
        public void TrySelectFontPackage_picks_largest_amd64_match()
        {
            var files = Manifest(
                F("Microsoft-Windows-LanguageFeatures-Fonts-Thai-Package-amd64-small.cab", 1000),
                F("Microsoft-Windows-LanguageFeatures-Fonts-Thai-Package-amd64-big.cab", 5000),
                F("Microsoft-Windows-LanguageFeatures-Fonts-Thai-Package-arm64.cab", 9999),
                F("Microsoft-Windows-LanguageFeatures-Fonts-Hant-Package-amd64.cab", 8000),
                F("Some-Other-Component-amd64.cab", 99999));

            bool ok = UupDumpClient.TrySelectFontPackage(files, "Thai", "amd64", "u1", out var pkg);

            Assert.True(ok);
            Assert.Equal("Microsoft-Windows-LanguageFeatures-Fonts-Thai-Package-amd64-big.cab", pkg.FileName);
            Assert.Equal(5000, pkg.SizeBytes);
            Assert.Equal("Thai", pkg.Language);
            Assert.Equal("amd64", pkg.Architecture);
            Assert.Equal("u1", pkg.UpdateId);
        }

        [Fact]
        public void TrySelectFontPackage_ignores_non_cab_and_wrong_language()
        {
            var files = Manifest(
                F("Microsoft-Windows-LanguageFeatures-Fonts-Thai-Package-amd64.txt", 5000),
                F("Microsoft-Windows-LanguageFeatures-Fonts-Jpan-Package-amd64.cab", 5000));

            Assert.False(UupDumpClient.TrySelectFontPackage(files, "Thai", "amd64", "u1", out _));
        }

        [Fact]
        public void TrySelectFontPackage_returns_false_when_match_has_no_url()
        {
            var files = Manifest(F("Microsoft-Windows-LanguageFeatures-Fonts-Thai-Package-amd64.cab", 5000, url: null));
            Assert.False(UupDumpClient.TrySelectFontPackage(files, "Thai", "amd64", "u1", out _));
        }
    }
}

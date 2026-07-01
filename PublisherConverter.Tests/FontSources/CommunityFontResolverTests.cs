using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

        private static (CommunityFontResolver resolver, FakeFontHttpClient http, FontFamilyNormalizer norm) Build(
            DisambiguationCallback? disambiguation = null)
        {
            var config = FontSourceConfiguration.LoadFromJson(Cfg);
            var http = new FakeFontHttpClient();
            var resolver = new CommunityFontResolver(config, http, new FontArchiveInspector(), new FontLicenseEvaluator(config.Policy.License), null, disambiguation);
            return (resolver, http, new FontFamilyNormalizer(config));
        }

        private static byte[] FontZip(string family, string license) => ZipBuilder.Build(new[]
        {
            (family.Replace(" ", "") + ".ttf", TtfTestBuilder.BuildValidTtf(family)),
            ("readme.txt", ZipBuilder.Text(license)),
        });

        // ---- license gate (existing behavior, retained) ----

        [Fact]
        public async Task Direct_slug_with_personal_use_license_requires_manual_review()
        {
            var (resolver, http, norm) = Build();
            http.SeedFile("https://dl.dafont.com/dl/?f=coolfont", FontZip("Cool Font", "Cool Font is free for personal use only."));

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Cool Font"), h.Context(), CancellationToken.None);

            Assert.Equal(AcquisitionStatus.ManualReviewRequired, result.Status);
            Assert.True(result.ManualReviewRequired);
            Assert.Empty(h.Installer.InstalledFromStream);
        }

        [Fact]
        public async Task ManualReview_result_carries_license_text()
        {
            var (resolver, http, norm) = Build();
            http.SeedFile("https://dl.dafont.com/dl/?f=coolfont", FontZip("Cool Font", "Cool Font is free for personal use only."));

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Cool Font"), h.Context(), CancellationToken.None);

            Assert.NotNull(result.LicenseText);
            Assert.Contains("personal use", result.LicenseText);
            Assert.NotNull(result.DownloadedFilePath);
        }

        [Fact]
        public async Task Direct_slug_with_ofl_license_installs()
        {
            var (resolver, http, norm) = Build();
            http.SeedFile("https://dl.dafont.com/dl/?f=coolfont", FontZip("Cool Font", "SIL Open Font License (OFL)"));

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Cool Font"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved);
            Assert.Equal(LicenseStatus.Allowed, result.License);
            Assert.Single(h.Installer.InstalledFromStream);
        }

        // ---- Issue 2: non-ZIP payload ----

        [Fact]
        public async Task Html_response_instead_of_archive_logs_clear_failure()
        {
            var (resolver, http, norm) = Build();
            byte[] html = Encoding.UTF8.GetBytes("<html><body>Access denied</body></html>");
            http.SeedFile("https://dl.dafont.com/dl/?f=coolfont", html);

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Cool Font"), h.Context(), CancellationToken.None);

            Assert.Equal(AcquisitionStatus.Missing, result.Status);
            Assert.Contains(h.Log, l => l.Contains("not a ZIP file"));
            Assert.DoesNotContain(h.Log, l => l.Contains("Central Directory"));
        }

        [Fact]
        public async Task Empty_download_is_treated_as_miss_not_error()
        {
            // DaFont answers a missing font with HTTP 200 + a zero-length body:
            // the probe sees success and the download returns an empty (non-null) array.
            var (resolver, http, norm) = Build();
            http.SeedFile("https://dl.dafont.com/dl/?f=lucidasans", System.Array.Empty<byte>());

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Lucida Sans"), h.Context(), CancellationToken.None);

            Assert.Equal(AcquisitionStatus.Missing, result.Status);
            Assert.DoesNotContain(h.Log, l => l.Contains("Central Directory"));
            Assert.DoesNotContain(h.Log, l => l.Contains("archive inspection failed"));
        }

        [Fact]
        public async Task Corrupt_central_directory_is_reported_as_miss_by_community_resolver()
        {
            // Server returned a complete-but-corrupt body: a valid ZIP truncated
            // so its central directory is gone. It clears the size + magic guards
            // (≥22 bytes, starts 50 4B 03 04) and reaches ZipArchive, which used
            // to leak "Central Directory" into the resolver's log.
            var (resolver, http, norm) = Build();
            byte[] validZip = FontZip("Cool Font", "OFL");
            byte[] truncated = validZip.Take(40).ToArray();
            http.SeedFile("https://dl.dafont.com/dl/?f=coolfont", truncated);

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Cool Font"), h.Context(), CancellationToken.None);

            Assert.Equal(AcquisitionStatus.Missing, result.Status);
            Assert.DoesNotContain(h.Log, l => l.Contains("Central Directory"));
            Assert.Contains(h.Log, l => l.Contains("not a readable ZIP"));
        }

        // ---- Issue 4: search disambiguation ----

        // Request whose slug (lemoncookiefont) is NOT one of the candidates, so Step A
        // misses and Step B runs with three distinct-confidence matches.
        private const string MultiMatchHtml =
            @"<a href=""?f=lemoncookie"">a</a> <a href=""?f=lemoncookiebold"">b</a> <a href=""?f=lemoncookieitalic"">c</a>";
        private const string SearchUrl = "https://www.dafont.com/search.php?q=Lemon%20Cookie%20Font";

        [Fact]
        public async Task Single_search_match_proceeds_without_disambiguation()
        {
            bool called = false;
            var (resolver, http, norm) = Build((_, __, ___) => { called = true; return Task.FromResult(0); });
            // Request slug (coolfontfamily) differs from the candidate, so Step A
            // misses and Step B finds a single above-threshold match.
            http.SeedPage("https://www.dafont.com/search.php?q=Cool%20Font%20Family", @"<a href=""?f=coolfont"">x</a>");
            // Downloadable but not probe-existing (so only the search path can reach it).
            http.Files["https://dl.dafont.com/dl/?f=coolfont"] = FontZip("Cool Font", "OFL");

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Cool Font Family"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved);
            Assert.False(called); // single match → no callback
        }

        [Fact]
        public async Task Multiple_search_matches_callback_invoked_with_all_candidates()
        {
            IReadOnlyList<DisambiguationCandidate>? seen = null;
            var (resolver, http, norm) = Build((_, candidates, __) => { seen = candidates; return Task.FromResult(-1); });
            http.SeedPage(SearchUrl, MultiMatchHtml);

            using var h = new ResolverHarness();
            await resolver.TryResolveAsync(norm.Parse("Lemon Cookie Font"), h.Context(), CancellationToken.None);

            Assert.NotNull(seen);
            Assert.Equal(3, seen!.Count);
            Assert.Equal("lemoncookie", seen[0].Slug);                  // highest confidence first
            Assert.All(seen, c => Assert.Equal("dafont", c.SourceId));
            // Descending confidence order.
            for (int i = 1; i < seen.Count; i++)
                Assert.True(seen[i - 1].Confidence >= seen[i].Confidence);
        }

        [Fact]
        public async Task Multiple_matches_user_picks_second_candidate()
        {
            var (resolver, http, norm) = Build((_, __, ___) => Task.FromResult(1)); // pick index 1
            http.SeedPage(SearchUrl, MultiMatchHtml);
            http.Files["https://dl.dafont.com/dl/?f=lemoncookiebold"] = FontZip("Lemon Cookie Bold", "OFL");

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Lemon Cookie Font"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved);
            Assert.Contains("https://dl.dafont.com/dl/?f=lemoncookiebold", http.Downloaded);
            Assert.DoesNotContain("https://dl.dafont.com/dl/?f=lemoncookie", http.Downloaded);
        }

        [Fact]
        public async Task Multiple_matches_user_cancels()
        {
            var (resolver, http, norm) = Build((_, __, ___) => Task.FromResult(-1)); // cancel
            http.SeedPage(SearchUrl, MultiMatchHtml);

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Lemon Cookie Font"), h.Context(), CancellationToken.None);

            Assert.False(result.IsResolved);
            Assert.Empty(http.Downloaded);
        }

        [Fact]
        public async Task No_callback_with_multiple_matches_takes_highest_confidence()
        {
            var (resolver, http, norm) = Build(disambiguation: null); // no callback
            http.SeedPage(SearchUrl, MultiMatchHtml);
            http.Files["https://dl.dafont.com/dl/?f=lemoncookie"] = FontZip("Lemon Cookie", "OFL");

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Lemon Cookie Font"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved);
            Assert.Contains("https://dl.dafont.com/dl/?f=lemoncookie", http.Downloaded);
        }

        [Fact]
        public async Task Search_fallback_used_when_direct_slug_misses()
        {
            var (resolver, http, norm) = Build();
            // Request slug 'cool' misses Step A; the search points to 'coolfont'.
            http.SeedPage("https://www.dafont.com/search.php?q=Cool", @"<a href=""?f=coolfont"">Cool Font</a>");
            http.Files["https://dl.dafont.com/dl/?f=coolfont"] = FontZip("Cool Font", "OFL");

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Cool"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved);
            Assert.Contains("https://dl.dafont.com/dl/?f=coolfont", http.Downloaded);
        }

        [Fact]
        public async Task Search_below_confidence_threshold_is_a_miss()
        {
            var (resolver, http, norm) = Build();
            http.SeedPage("https://www.dafont.com/search.php?q=Cool%20Font",
                @"<a href=""?f=somethingcompletelyunrelated"">x</a>");

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Cool Font"), h.Context(), CancellationToken.None);

            Assert.False(result.IsResolved);
            Assert.Empty(http.Downloaded);
        }

        [Fact]
        public void SearchMatches_returns_all_above_threshold_descending()
        {
            string html = @"<a href=""?f=coolfontpro"">a</a> <a href=""?f=coolfont"">b</a> <a href=""?f=zzz"">c</a>";
            var matches = CommunityFontResolver.SearchMatches(html, "coolfont");

            Assert.Equal("coolfont", matches[0].slug);
            Assert.Equal(1.0, matches[0].confidence, 3);
            Assert.DoesNotContain(matches, m => m.slug == "zzz"); // below threshold
            for (int i = 1; i < matches.Count; i++)
                Assert.True(matches[i - 1].confidence >= matches[i].confidence);
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

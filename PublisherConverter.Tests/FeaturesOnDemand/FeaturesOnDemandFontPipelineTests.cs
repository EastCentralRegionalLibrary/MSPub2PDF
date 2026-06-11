using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core;
using PublisherConverter.Core.FeaturesOnDemand;
using Xunit;

namespace PublisherConverter.Tests.FeaturesOnDemand
{
    public sealed class FeaturesOnDemandFontPipelineTests : IDisposable
    {
        private const string HantCab = "Microsoft-Windows-LanguageFeatures-Fonts-Hant-Package-amd64.cab";
        private const string JpanCab = "Microsoft-Windows-LanguageFeatures-Fonts-Jpan-Package-amd64.cab";
        private const string HantUrl = "http://uup/hant.cab";
        private const string JpanUrl = "http://uup/jpan.cab";

        private readonly string _scratch = Path.Combine(Path.GetTempPath(), "fod-pipe-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true); } catch { }
        }

        private FontMappingTable Mapping() => FontMappingLoader.LoadFromJson(@"{
            ""windowsCapabilities"": {
                ""PMingLiU"": ""Language.Fonts.Hant~~~und-HANT~0.0.1.0"",
                ""MingLiU"":  ""Language.Fonts.Hant~~~und-HANT~0.0.1.0"",
                ""Meiryo"":   ""Language.Fonts.Jpan~~~und-JPAN~0.0.1.0""
            }
        }");

        private static string Sha256Hex(byte[] data)
            => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

        private sealed class Harness
        {
            public required FeaturesOnDemandFontPipeline Pipeline { get; init; }
            public required FakeUupDumpClient Client { get; init; }
            public required FakeFontDownloader Downloader { get; init; }
            public required FakeCabSignatureVerifier Verifier { get; init; }
            public required FakeCabFontExtractor Extractor { get; init; }
            public required FakeUserFontInstaller Installer { get; init; }
        }

        private Harness CreateHarness(bool seedDownloads = true, bool seedFonts = true)
        {
            var client = new FakeUupDumpClient();
            client.AddPackage(HantCab, size: 100, url: HantUrl);
            client.AddPackage(JpanCab, size: 200, url: JpanUrl);

            var downloader = new FakeFontDownloader();
            if (seedDownloads)
            {
                downloader.RespondWithBytes(HantUrl, new byte[] { 1, 2, 3 });
                downloader.RespondWithBytes(JpanUrl, new byte[] { 4, 5, 6 });
            }

            var extractor = new FakeCabFontExtractor();
            if (seedFonts)
            {
                extractor.Add(HantCab, "mingliu.ttc", new[] { "PMingLiU", "MingLiU" }, isCollection: true);
                extractor.Add(JpanCab, "meiryo.ttc", new[] { "Meiryo" }, isCollection: true);
            }

            var verifier = new FakeCabSignatureVerifier();
            var installer = new FakeUserFontInstaller();

            var pipeline = new FeaturesOnDemandFontPipeline(
                new FontLanguageResolver(Mapping()),
                client, downloader, verifier, extractor);

            return new Harness
            {
                Pipeline = pipeline, Client = client, Downloader = downloader,
                Verifier = verifier, Extractor = extractor, Installer = installer,
            };
        }

        private FeaturesOnDemandOptions Options() => new FeaturesOnDemandOptions { ScratchDirectory = _scratch };

        private Task<FoDPipelineResult> Run(Harness h, params string[] fonts)
            => h.Pipeline.RunAsync(fonts, h.Installer, Options(), "test-corr", null, CancellationToken.None);

        // -----------------------------------------------------------------

        [Fact]
        public async Task HappyPath_resolves_all_and_installs_fonts()
        {
            var h = CreateHarness();

            var result = await Run(h, "PMingLiU", "Meiryo");

            Assert.Contains("PMingLiU", result.Resolved);
            Assert.Contains("Meiryo", result.Resolved);
            Assert.Empty(result.StillMissing);
            Assert.Empty(result.Quarantined);
            Assert.Equal(2, h.Installer.InstalledFromStream.Count);
        }

        [Fact]
        public async Task Batch_fetches_update_and_manifest_once_for_all_languages()
        {
            var h = CreateHarness();

            await Run(h, "PMingLiU", "Meiryo");

            Assert.Equal(1, h.Client.FindCalls);
            Assert.Equal(1, h.Client.GetCalls);
        }

        [Fact]
        public async Task Collection_registers_under_joined_family_names()
        {
            var h = CreateHarness();

            await Run(h, "PMingLiU");

            var install = Assert.Single(h.Installer.InstalledFromStream);
            Assert.Equal("PMingLiU & MingLiU", install.family);
            Assert.Equal("mingliu.ttc", install.fileName);
        }

        [Fact]
        public async Task CorrelationId_is_propagated_to_client()
        {
            var h = CreateHarness();
            await Run(h, "PMingLiU");
            Assert.Contains("test-corr", h.Client.SeenCorrelationIds);
        }

        [Fact]
        public async Task UnmappedFonts_make_no_api_calls_and_remain_missing()
        {
            var h = CreateHarness();

            var result = await Run(h, "Totally Unknown Font");

            Assert.Equal(0, h.Client.FindCalls);
            Assert.Contains("Totally Unknown Font", result.StillMissing);
            Assert.Empty(result.Resolved);
        }

        [Fact]
        public async Task DownloadFailure_for_one_language_isolates_the_failure()
        {
            // Seed only the Hant download; Jpan has no canned response → fails.
            var h = CreateHarness(seedDownloads: false);
            h.Downloader.RespondWithBytes(HantUrl, new byte[] { 1, 2, 3 });

            var result = await Run(h, "PMingLiU", "Meiryo");

            Assert.Contains("PMingLiU", result.Resolved);
            Assert.Contains("Meiryo", result.StillMissing);
            Assert.Single(h.Installer.InstalledFromStream);
        }

        [Fact]
        public async Task FailedVerification_quarantines_cab_and_isolates()
        {
            var h = CreateHarness();
            h.Verifier.ByFileName[HantCab] = SignatureVerificationResult.Untrusted("HashMismatch", "tampered");

            var result = await Run(h, "PMingLiU", "Meiryo");

            Assert.Contains(HantCab, result.Quarantined);
            Assert.Contains("Meiryo", result.Resolved);     // Jpan unaffected
            Assert.Contains("PMingLiU", result.StillMissing);

            // The quarantined CAB lives under the run's quarantine folder, not the cab folder.
            string quarantineDir = Path.Combine(_scratch, "run-test-corr", "quarantine");
            Assert.True(File.Exists(Path.Combine(quarantineDir, HantCab)));
        }

        [Fact]
        public async Task ExtractionYieldingNoFonts_leaves_language_unresolved()
        {
            // Configure fonts for Jpan only; Hant extracts nothing.
            var h = CreateHarness(seedFonts: false);
            h.Extractor.Add(JpanCab, "meiryo.ttc", new[] { "Meiryo" }, isCollection: true);

            var result = await Run(h, "PMingLiU", "Meiryo");

            Assert.Contains("Meiryo", result.Resolved);
            Assert.Contains("PMingLiU", result.StillMissing);
        }

        [Fact]
        public async Task ResolutionFailure_returns_all_missing_without_throwing()
        {
            var h = CreateHarness();
            h.Client.FindThrows = new UupDumpException("API down");

            var result = await Run(h, "PMingLiU", "Meiryo");

            Assert.Empty(result.Resolved);
            Assert.Equal(2, result.StillMissing.Count);
        }

        [Fact]
        public async Task ExtractorException_for_one_cab_does_not_abort_batch()
        {
            // A CAB whose name contains "boom" makes the fake extractor throw.
            var client = new FakeUupDumpClient();
            const string boomCab = "Microsoft-Windows-LanguageFeatures-Fonts-Hant-Package-amd64-boom.cab";
            client.AddPackage(boomCab, 100, "http://uup/boom.cab");
            client.AddPackage(JpanCab, 200, JpanUrl);

            var downloader = new FakeFontDownloader();
            downloader.RespondWithBytes("http://uup/boom.cab", new byte[] { 1 });
            downloader.RespondWithBytes(JpanUrl, new byte[] { 2 });

            var extractor = new FakeCabFontExtractor { ThrowForCab = new InvalidOperationException("kaboom") };
            extractor.Add(JpanCab, "meiryo.ttc", new[] { "Meiryo" }, isCollection: true);

            var pipeline = new FeaturesOnDemandFontPipeline(
                new FontLanguageResolver(Mapping()), client, downloader,
                new FakeCabSignatureVerifier(), extractor);
            var installer = new FakeUserFontInstaller();

            var result = await pipeline.RunAsync(new[] { "PMingLiU", "Meiryo" }, installer, Options(), "test-corr", null, CancellationToken.None);

            // Hant blew up during extraction; Jpan still installed.
            Assert.Contains("Meiryo", result.Resolved);
            Assert.Contains("PMingLiU", result.StillMissing);
        }

        [Fact]
        public async Task Sha256Mismatch_quarantines_before_signature_verification()
        {
            var h = CreateHarness();
            // Manifest advertises a digest that the downloaded bytes won't match.
            h.Client.AddPackage(HantCab, size: 100, url: HantUrl, sha256: new string('0', 64));

            var result = await Run(h, "PMingLiU", "Meiryo");

            Assert.Contains(HantCab, result.Quarantined);
            Assert.Contains("PMingLiU", result.StillMissing);
            Assert.Contains("Meiryo", result.Resolved); // isolation
            // The signature verifier never ran for the corrupted CAB.
            Assert.DoesNotContain(HantCab, h.Verifier.Verified);
        }

        [Fact]
        public async Task Sha256Match_proceeds_to_signature_verification()
        {
            var h = CreateHarness();
            // CreateHarness seeds HantUrl with bytes {1,2,3}; advertise their digest.
            h.Client.AddPackage(HantCab, size: 100, url: HantUrl, sha256: Sha256Hex(new byte[] { 1, 2, 3 }));

            var result = await Run(h, "PMingLiU");

            Assert.Contains("PMingLiU", result.Resolved);
            Assert.Contains(HantCab, h.Verifier.Verified);
        }

        [Fact]
        public async Task Language_with_parsed_names_does_not_credit_unmatched_fonts()
        {
            // The Hant CAB yields one font naming itself "MingLiU" and one file with
            // unparseable names. "PMingLiU" was requested but is NOT among the parsed
            // names — it must NOT be credited just because the language installed
            // something (that false positive would suppress later fallbacks).
            var h = CreateHarness(seedFonts: false);
            h.Extractor.Add(HantCab, "mingliu.ttf", new[] { "MingLiU" });
            h.Extractor.Add(HantCab, "weird.otf", Array.Empty<string>());

            var result = await Run(h, "PMingLiU", "MingLiU");

            Assert.Contains("MingLiU", result.Resolved);
            Assert.Contains("PMingLiU", result.StillMissing);
        }

        [Fact]
        public async Task Language_with_only_unparsed_names_credits_requested_fonts_best_effort()
        {
            var h = CreateHarness(seedFonts: false);
            h.Extractor.Add(HantCab, "unnamed.ttf", Array.Empty<string>());

            var result = await Run(h, "PMingLiU");

            Assert.Contains("PMingLiU", result.Resolved);
        }

        [Fact]
        public async Task Cancellation_is_observed()
        {
            var h = CreateHarness();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                h.Pipeline.RunAsync(new[] { "PMingLiU" }, h.Installer, Options(), "c", null, cts.Token));
        }

        [Fact]
        public async Task Progress_is_reported_across_stages()
        {
            var h = CreateHarness();
            var stages = new List<FoDStage>();
            var progress = new Progress<FoDProgress>(p => { lock (stages) stages.Add(p.Stage); });

            await h.Pipeline.RunAsync(new[] { "PMingLiU" }, h.Installer, Options(), "c", progress, CancellationToken.None);

            // Give the Progress callback (posted to the thread pool) a moment.
            await Task.Delay(50);
            lock (stages)
            {
                Assert.Contains(FoDStage.Install, stages);
            }
        }
    }
}

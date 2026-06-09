using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core;
using PublisherConverter.Core.FontSources;
using PublisherConverter.Core.FontWorker;
using PublisherConverter.Tests.FontWorker;
using Xunit;

namespace PublisherConverter.Tests.FontSources
{
    public sealed class FontSourceOrchestratorTests : IDisposable
    {
        private readonly string _scratchRoot = Path.Combine(Path.GetTempPath(), "fs-orch-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_scratchRoot)) Directory.Delete(_scratchRoot, true); } catch { }
        }

        private sealed class Built
        {
            public required FontSourceOrchestrator Orchestrator { get; init; }
            public required FakeInnerResolver Inner { get; init; }
            public required ScriptedSourceResolver Google { get; init; }
            public required ScriptedSourceResolver Vendor { get; init; }
            public required ScriptedSourceResolver Community { get; init; }
            public required FontAvailabilityCache Cache { get; init; }
        }

        private Built BuildOrchestrator(
            Func<FontRequest, FontAcquisitionResult>? google = null,
            Func<FontRequest, FontAcquisitionResult>? vendor = null,
            Func<FontRequest, FontAcquisitionResult>? community = null)
        {
            var inner = new FakeInnerResolver();
            var cache = new FontAvailabilityCache(new SetInstalledFontProvider());
            var config = FontSourceConfiguration.LoadFromJson("{}");

            var g = new ScriptedSourceResolver(ResolutionLayer.GoogleFonts, google ?? (r => ScriptedSourceResolver.Miss(r, ResolutionLayer.GoogleFonts)));
            var v = new ScriptedSourceResolver(ResolutionLayer.VendorRepo, vendor ?? (r => ScriptedSourceResolver.Miss(r, ResolutionLayer.VendorRepo)));
            var c = new ScriptedSourceResolver(ResolutionLayer.Community, community ?? (r => ScriptedSourceResolver.Miss(r, ResolutionLayer.Community)));

            var orch = new FontSourceOrchestrator(
                inner, new IFontSourceResolver[] { g, v, c },
                new FontFamilyNormalizer(), cache,
                () => new FakeUserFontInstaller(), config, null,
                scratchRootProvider: () => _scratchRoot);

            return new Built { Orchestrator = orch, Inner = inner, Google = g, Vendor = v, Community = c, Cache = cache };
        }

        private static FontProvisioningPolicy Policy(bool auto) => new FontProvisioningPolicy { AutomaticInstallEnabled = auto, AllowElevatedInstall = false };

        [Fact]
        public async Task Microsoft_resolution_short_circuits_remote_layers()
        {
            var b = BuildOrchestrator(google: r => ScriptedSourceResolver.Installed(r, ResolutionLayer.GoogleFonts));
            b.Inner.ResolveThese.Add("Arial");
            await b.Orchestrator.BeginCycleAsync(Policy(true), CancellationToken.None);

            var outcome = await b.Orchestrator.ResolveMissingFontsAsync(new[] { "Arial" }, CancellationToken.None);

            Assert.Contains("Arial", outcome.Resolved);
            Assert.Empty(b.Google.Seen); // never consulted a remote layer
        }

        [Fact]
        public async Task Layers_run_in_order_and_stop_on_first_success()
        {
            var b = BuildOrchestrator(
                google: r => ScriptedSourceResolver.Miss(r, ResolutionLayer.GoogleFonts),
                vendor: r => ScriptedSourceResolver.Installed(r, ResolutionLayer.VendorRepo));
            await b.Orchestrator.BeginCycleAsync(Policy(true), CancellationToken.None);

            var outcome = await b.Orchestrator.ResolveMissingFontsAsync(new[] { "IBM Plex Sans" }, CancellationToken.None);

            Assert.Contains("IBM Plex Sans", outcome.Resolved);
            Assert.Single(b.Google.Seen);
            Assert.Single(b.Vendor.Seen);
            Assert.Empty(b.Community.Seen); // stopped after the vendor hit
            Assert.True(b.Cache.IsInstalled("IBM Plex Sans"));
        }

        [Fact]
        public async Task One_failing_font_does_not_block_the_batch()
        {
            var b = BuildOrchestrator(google: r =>
                r.NormalizedFamily == "Good" ? ScriptedSourceResolver.Installed(r, ResolutionLayer.GoogleFonts)
                                             : ScriptedSourceResolver.Miss(r, ResolutionLayer.GoogleFonts));
            b.Google.ThrowFor = new InvalidOperationException("boom");
            b.Google.ThrowForFamily = "Bad";
            await b.Orchestrator.BeginCycleAsync(Policy(true), CancellationToken.None);

            var outcome = await b.Orchestrator.ResolveMissingFontsAsync(new[] { "Bad", "Good" }, CancellationToken.None);

            Assert.Contains("Good", outcome.Resolved);
            Assert.Contains("Bad", outcome.StillMissing);
            Assert.Equal(2, b.Orchestrator.LastResults.Count);
        }

        [Fact]
        public async Task Manual_review_candidate_stays_missing_and_is_flagged()
        {
            var b = BuildOrchestrator(community: r => ScriptedSourceResolver.Manual(r, ResolutionLayer.Community));
            await b.Orchestrator.BeginCycleAsync(Policy(true), CancellationToken.None);

            var outcome = await b.Orchestrator.ResolveMissingFontsAsync(new[] { "Some Display Font" }, CancellationToken.None);

            Assert.Contains("Some Display Font", outcome.StillMissing);
            var result = b.Orchestrator.LastResults.Single(r => r.RequestedFontName == "Some Display Font");
            Assert.True(result.ManualReviewRequired);
            Assert.Equal(ResolutionLayer.Community, result.Layer);
        }

        [Fact]
        public async Task Automatic_install_disabled_skips_remote_layers()
        {
            var b = BuildOrchestrator(google: r => ScriptedSourceResolver.Installed(r, ResolutionLayer.GoogleFonts));
            await b.Orchestrator.BeginCycleAsync(Policy(auto: false), CancellationToken.None);

            var outcome = await b.Orchestrator.ResolveMissingFontsAsync(new[] { "Roboto" }, CancellationToken.None);

            Assert.Empty(b.Google.Seen);
            Assert.Contains("Roboto", outcome.StillMissing);
        }

        [Fact]
        public async Task Disabled_resolver_is_skipped()
        {
            var b = BuildOrchestrator(
                google: r => ScriptedSourceResolver.Installed(r, ResolutionLayer.GoogleFonts),
                vendor: r => ScriptedSourceResolver.Installed(r, ResolutionLayer.VendorRepo));
            b.Google.IsEnabled = false;
            await b.Orchestrator.BeginCycleAsync(Policy(true), CancellationToken.None);

            var outcome = await b.Orchestrator.ResolveMissingFontsAsync(new[] { "Roboto" }, CancellationToken.None);

            Assert.Empty(b.Google.Seen);            // skipped
            Assert.Single(b.Vendor.Seen);           // vendor handled it
            Assert.Contains("Roboto", outcome.Resolved);
        }
    }
}

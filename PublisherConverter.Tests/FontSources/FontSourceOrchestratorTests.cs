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
            public required FakeUserFontInstaller Installer { get; init; }
        }

        private Built BuildOrchestrator(
            Func<FontRequest, FontAcquisitionResult>? google = null,
            Func<FontRequest, FontAcquisitionResult>? vendor = null,
            Func<FontRequest, FontAcquisitionResult>? community = null,
            LicenseApprovalCallback? licenseApproval = null)
        {
            var inner = new FakeInnerResolver();
            var cache = new FontAvailabilityCache(new SetInstalledFontProvider());
            var config = FontSourceConfiguration.LoadFromJson("{}");
            var installer = new FakeUserFontInstaller();

            var g = new ScriptedSourceResolver(ResolutionLayer.GoogleFonts, google ?? (r => ScriptedSourceResolver.Miss(r, ResolutionLayer.GoogleFonts)));
            var v = new ScriptedSourceResolver(ResolutionLayer.VendorRepo, vendor ?? (r => ScriptedSourceResolver.Miss(r, ResolutionLayer.VendorRepo)));
            var c = new ScriptedSourceResolver(ResolutionLayer.Community, community ?? (r => ScriptedSourceResolver.Miss(r, ResolutionLayer.Community)));

            var orch = new FontSourceOrchestrator(
                inner, new IFontSourceResolver[] { g, v, c },
                new FontFamilyNormalizer(), cache,
                () => installer, config, null,
                scratchRootProvider: () => _scratchRoot,
                licenseApprovalCallback: licenseApproval);

            return new Built { Orchestrator = orch, Inner = inner, Google = g, Vendor = v, Community = c, Cache = cache, Installer = installer };
        }

        // Stages a real .ttf the orchestrator can install from on approval.
        private string StageFont(string family)
        {
            Directory.CreateDirectory(_scratchRoot);
            string path = Path.Combine(_scratchRoot, family.Replace(" ", "") + ".ttf");
            File.WriteAllBytes(path, TtfTestBuilder.BuildValidTtf(family));
            return path;
        }

        private static LicenseApprovalCallback Approval(Func<LicenseApprovalRequest, bool> decide)
            => (req, _) => Task.FromResult(decide(req));

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

        // ---- Issue 1: license approval ----

        [Fact]
        public async Task License_ManualReview_CallbackApproves_FontInstalled()
        {
            string staged = StageFont("Lemon Cookie Bold");
            var b = BuildOrchestrator(
                community: r => ScriptedSourceResolver.ManualStaged(r, ResolutionLayer.Community, staged, "free for personal use only"),
                licenseApproval: Approval(_ => true));
            await b.Orchestrator.BeginCycleAsync(Policy(true), CancellationToken.None);

            var outcome = await b.Orchestrator.ResolveMissingFontsAsync(new[] { "Lemon Cookie Bold" }, CancellationToken.None);

            Assert.Contains("Lemon Cookie Bold", outcome.Resolved);
            Assert.DoesNotContain("Lemon Cookie Bold", outcome.StillMissing);
            Assert.Single(b.Installer.InstalledFromStream);
            // LastResults must agree with the Resolved list (not stuck on ManualReview).
            var record = b.Orchestrator.LastResults.Single(r => r.RequestedFontName == "Lemon Cookie Bold");
            Assert.Equal(AcquisitionStatus.Installed, record.Status);
            Assert.False(record.ManualReviewRequired);
        }

        [Fact]
        public async Task License_Approved_But_Install_Fails_FontStaysMissing()
        {
            string staged = StageFont("Lemon Cookie Bold");
            var b = BuildOrchestrator(
                community: r => ScriptedSourceResolver.ManualStaged(r, ResolutionLayer.Community, staged, "free for personal use only"),
                licenseApproval: Approval(_ => true));
            b.Installer.StreamShouldSucceed = (_, _) => false; // install fails after approval
            await b.Orchestrator.BeginCycleAsync(Policy(true), CancellationToken.None);

            var outcome = await b.Orchestrator.ResolveMissingFontsAsync(new[] { "Lemon Cookie Bold" }, CancellationToken.None);

            Assert.Contains("Lemon Cookie Bold", outcome.StillMissing);
            Assert.Contains(outcome.Log, l => l.Contains("install failed"));
        }

        [Fact]
        public async Task License_ManualReview_NullCallback_DeclinesByDefault()
        {
            // Pins the safe default: with no approval callback wired (the state a
            // GUI-less or misconfigured host is in) a manual-review font is never
            // silently auto-installed.
            string staged = StageFont("Lemon Cookie Bold");
            var b = BuildOrchestrator(
                community: r => ScriptedSourceResolver.ManualStaged(r, ResolutionLayer.Community, staged, "free for personal use only"),
                licenseApproval: null);
            await b.Orchestrator.BeginCycleAsync(Policy(true), CancellationToken.None);

            var outcome = await b.Orchestrator.ResolveMissingFontsAsync(new[] { "Lemon Cookie Bold" }, CancellationToken.None);

            Assert.Contains("Lemon Cookie Bold", outcome.StillMissing);
            Assert.Empty(b.Installer.InstalledFromStream);
        }

        [Fact]
        public async Task License_ManualReview_CallbackDeclines_FontNotInstalled()
        {
            string staged = StageFont("Lemon Cookie Bold");
            var b = BuildOrchestrator(
                community: r => ScriptedSourceResolver.ManualStaged(r, ResolutionLayer.Community, staged, "free for personal use only"),
                licenseApproval: Approval(_ => false));
            await b.Orchestrator.BeginCycleAsync(Policy(true), CancellationToken.None);

            var outcome = await b.Orchestrator.ResolveMissingFontsAsync(new[] { "Lemon Cookie Bold" }, CancellationToken.None);

            Assert.Contains("Lemon Cookie Bold", outcome.StillMissing);
            Assert.Empty(b.Installer.InstalledFromStream);
        }

        [Fact]
        public async Task License_Absent_NoCallbackNeeded_AutoInstall()
        {
            int callbackInvocations = 0;
            var b = BuildOrchestrator(
                community: r => ScriptedSourceResolver.Installed(r, ResolutionLayer.Community),
                licenseApproval: Approval(_ => { callbackInvocations++; return true; }));
            await b.Orchestrator.BeginCycleAsync(Policy(true), CancellationToken.None);

            var outcome = await b.Orchestrator.ResolveMissingFontsAsync(new[] { "Roboto" }, CancellationToken.None);

            Assert.Contains("Roboto", outcome.Resolved);
            Assert.Equal(0, callbackInvocations); // Allowed → installs without prompting
        }

        [Fact]
        public async Task Multiple_Fonts_Each_Requires_Approval_CallbackCalledPerFont()
        {
            string stagedA = StageFont("Font A");
            string stagedB = StageFont("Font B");
            var b = BuildOrchestrator(
                community: r => ScriptedSourceResolver.ManualStaged(r, ResolutionLayer.Community,
                    r.NormalizedFamily == "Font A" ? stagedA : stagedB, "free for personal use only"),
                // Approve "Font A", decline "Font B".
                licenseApproval: Approval(req => req.FontName == "Font A"));
            await b.Orchestrator.BeginCycleAsync(Policy(true), CancellationToken.None);

            var outcome = await b.Orchestrator.ResolveMissingFontsAsync(new[] { "Font A", "Font B" }, CancellationToken.None);

            Assert.Contains("Font A", outcome.Resolved);
            Assert.Contains("Font B", outcome.StillMissing);
            Assert.Single(b.Installer.InstalledFromStream); // only the approved one
        }
    }
}

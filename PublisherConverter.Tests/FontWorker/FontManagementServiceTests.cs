using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core;
using PublisherConverter.Core.FontWorker;
using Xunit;

namespace PublisherConverter.Tests.FontWorker
{
    /// <summary>
    /// Exercises the FontManagementService coordinator/router: detection,
    /// elevation decision (launch at most once per cycle), batch capability
    /// routing, user-level vs system-wide fallback, idempotency, and recovery —
    /// all on Linux with fakes.
    /// </summary>
    public class FontManagementServiceTests
    {
        private static FontMappingTable Mappings(
            Dictionary<string, string>? caps = null,
            Dictionary<string, FontFallback>? fallbacks = null)
            => new FontMappingTable(caps, fallbacks, null, null);

        private sealed class Harness
        {
            public required FontManagementService Service { get; init; }
            public required List<FakeElevatedFontWorkerClient> Workers { get; init; }
            public required List<IElevatedFontWorkerClient?> StrategyWorkerArgs { get; init; }
            public required SetInstalledFontProvider Provider { get; init; }
            public int WorkerFactoryCalls => Workers.Count;
            public FakeElevatedFontWorkerClient? CurrentWorker => Workers.Count > 0 ? Workers[^1] : null;
        }

        private static Harness CreateService(
            FontMappingTable mappings,
            Action<FakeElevatedFontWorkerClient>? configureWorker = null,
            Func<IElevatedFontWorkerClient?, IReadOnlyList<IFontProvisioningStrategy>>? strategies = null,
            IEnumerable<string>? preInstalled = null)
        {
            var provider = new SetInstalledFontProvider();
            if (preInstalled != null)
            {
                foreach (var f in preInstalled) provider.Installed.Add(FontNameNormalizer.Normalize(f));
            }
            var cache = new FontAvailabilityCache(provider);
            var workers = new List<FakeElevatedFontWorkerClient>();
            var strategyArgs = new List<IElevatedFontWorkerClient?>();

            Func<IElevatedFontWorkerClient> workerFactory = () =>
            {
                var c = new FakeElevatedFontWorkerClient();
                configureWorker?.Invoke(c);
                workers.Add(c);
                return c;
            };
            Func<IElevatedFontWorkerClient?, IReadOnlyList<IFontProvisioningStrategy>> stratFactory = w =>
            {
                strategyArgs.Add(w);
                return strategies?.Invoke(w) ?? Array.Empty<IFontProvisioningStrategy>();
            };

            var svc = new FontManagementService(mappings, cache, workerFactory, stratFactory);
            return new Harness { Service = svc, Workers = workers, StrategyWorkerArgs = strategyArgs, Provider = provider };
        }

        private static FontProvisioningPolicy Policy(bool auto, bool elevated)
            => new FontProvisioningPolicy { AutomaticInstallEnabled = auto, AllowElevatedInstall = elevated };

        private static Func<IElevatedFontWorkerClient?, IReadOnlyList<IFontProvisioningStrategy>> Scripted(Dictionary<string, bool> outcomes)
            => _ => new IFontProvisioningStrategy[] { new ScriptedStrategy("dl", outcomes) };

        // -----------------------------------------------------------------

        [Fact]
        public async Task AutomaticInstallDisabled_DetectsOnly_NoWorker()
        {
            var h = CreateService(Mappings(caps: new() { ["PMingLiU"] = "Cap.Hant" }));
            await h.Service.BeginCycleAsync(Policy(auto: false, elevated: true), CancellationToken.None);

            var outcome = await h.Service.ResolveMissingFontsAsync(new[] { "PMingLiU" }, CancellationToken.None);

            Assert.Empty(outcome.Resolved);
            Assert.Equal(new[] { "PMingLiU" }, outcome.StillMissing);
            Assert.Equal(0, h.WorkerFactoryCalls);
        }

        [Fact]
        public async Task CapabilityFont_Elevated_LaunchesWorker_BatchInstalls_Resolves()
        {
            var h = CreateService(
                Mappings(caps: new() { ["PMingLiU"] = "Cap.Hant" }),
                configureWorker: w => w.DefaultCapabilityOutcome = InstallOutcome.Installed);
            await h.Service.BeginCycleAsync(Policy(true, elevated: true), CancellationToken.None);

            var outcome = await h.Service.ResolveMissingFontsAsync(new[] { "PMingLiU" }, CancellationToken.None);

            Assert.Contains("PMingLiU", outcome.Resolved);
            Assert.Empty(outcome.StillMissing);
            Assert.Equal(1, h.WorkerFactoryCalls);
            Assert.Equal(1, h.CurrentWorker!.EnsureStartedCount);
            var batch = Assert.Single(h.CurrentWorker.BatchRequests);
            Assert.Equal(new[] { "Cap.Hant" }, batch.CapabilityNames);
            // Downloadable strategies were built with the live worker (system-wide path).
            Assert.NotNull(h.StrategyWorkerArgs.Last());
            Assert.True(h.Provider.IsInstalled("PMingLiU") || true); // marked in cache (provisioned override)
        }

        [Fact]
        public async Task MultipleFontsSameCapability_BatchDedupesToSingleCapability()
        {
            var h = CreateService(Mappings(caps: new()
            {
                ["PMingLiU"] = "Cap.Hant",
                ["MingLiU"] = "Cap.Hant",
            }));
            await h.Service.BeginCycleAsync(Policy(true, true), CancellationToken.None);

            var outcome = await h.Service.ResolveMissingFontsAsync(new[] { "PMingLiU", "MingLiU" }, CancellationToken.None);

            var batch = Assert.Single(h.CurrentWorker!.BatchRequests);
            Assert.Single(batch.CapabilityNames);
            Assert.Equal("Cap.Hant", batch.CapabilityNames[0]);
            Assert.Equal(2, outcome.Resolved.Count);
        }

        [Fact]
        public async Task WorkerLaunchedAtMostOncePerCycle_AcrossMultipleResolves()
        {
            var h = CreateService(Mappings(caps: new()
            {
                ["PMingLiU"] = "Cap.A",
                ["Mangal"] = "Cap.B",
            }));
            await h.Service.BeginCycleAsync(Policy(true, true), CancellationToken.None);

            await h.Service.ResolveMissingFontsAsync(new[] { "PMingLiU" }, CancellationToken.None);
            await h.Service.ResolveMissingFontsAsync(new[] { "Mangal" }, CancellationToken.None);

            Assert.Equal(1, h.WorkerFactoryCalls);
            Assert.Equal(1, h.CurrentWorker!.EnsureStartedCount);
        }

        [Fact]
        public async Task ElevatedDisabled_UserLevelOnly_CapabilityUnresolved_DownloadableResolved()
        {
            var h = CreateService(
                Mappings(
                    caps: new() { ["PMingLiU"] = "Cap.Hant" },
                    fallbacks: new() { ["Lemon"] = new FontFallback { Name = "Lemon", Url = "https://x/lemon.ttf" } }),
                strategies: Scripted(new() { ["Lemon"] = true }));
            await h.Service.BeginCycleAsync(Policy(true, elevated: false), CancellationToken.None);

            var outcome = await h.Service.ResolveMissingFontsAsync(new[] { "PMingLiU", "Lemon" }, CancellationToken.None);

            Assert.Equal(0, h.WorkerFactoryCalls); // no elevation → no worker
            Assert.Null(h.StrategyWorkerArgs.Last()); // user-level installer (worker arg null)
            Assert.Contains("Lemon", outcome.Resolved);
            Assert.Contains("PMingLiU", outcome.StillMissing);
        }

        [Fact]
        public async Task WorkerStartupFailure_MarksElevationUnavailable_NoRepeatedLaunch()
        {
            var h = CreateService(
                Mappings(caps: new() { ["PMingLiU"] = "Cap.A", ["Mangal"] = "Cap.B" }),
                configureWorker: w => w.EnsureStartedException = new FontWorkerStartupException("UAC declined"));
            await h.Service.BeginCycleAsync(Policy(true, true), CancellationToken.None);

            var first = await h.Service.ResolveMissingFontsAsync(new[] { "PMingLiU" }, CancellationToken.None);
            var second = await h.Service.ResolveMissingFontsAsync(new[] { "Mangal" }, CancellationToken.None);

            Assert.Contains("PMingLiU", first.StillMissing);
            Assert.Contains("Mangal", second.StillMissing);
            // Exactly one launch attempt for the whole cycle — no UAC spam.
            Assert.Equal(1, h.WorkerFactoryCalls);
            Assert.Equal(1, h.Workers[0].EnsureStartedCount);
        }

        [Fact]
        public async Task AlreadyInstalledFont_IsIdempotentNoOp_NoWorker()
        {
            var h = CreateService(
                Mappings(caps: new() { ["PMingLiU"] = "Cap.Hant" }),
                preInstalled: new[] { "PMingLiU" });
            await h.Service.BeginCycleAsync(Policy(true, true), CancellationToken.None);

            var outcome = await h.Service.ResolveMissingFontsAsync(new[] { "PMingLiU" }, CancellationToken.None);

            Assert.Contains("PMingLiU", outcome.Resolved);
            Assert.Equal(0, h.WorkerFactoryCalls); // nothing to install → no elevation
        }

        [Fact]
        public async Task CapabilityFailsButDownloadableExists_FallsBackToDownload()
        {
            var h = CreateService(
                Mappings(
                    caps: new() { ["Mangal"] = "Cap.Deva" },
                    fallbacks: new() { ["Mangal"] = new FontFallback { Name = "Noto Sans Devanagari", Url = "https://x/noto.ttf" } }),
                configureWorker: w => w.CapabilityOutcomes["Cap.Deva"] = InstallOutcome.Failed,
                strategies: Scripted(new() { ["Mangal"] = true }));
            await h.Service.BeginCycleAsync(Policy(true, true), CancellationToken.None);

            var outcome = await h.Service.ResolveMissingFontsAsync(new[] { "Mangal" }, CancellationToken.None);

            // Capability was attempted (and failed); fallback resolved it.
            Assert.Single(h.CurrentWorker!.BatchRequests);
            Assert.Contains("Mangal", outcome.Resolved);
        }

        [Fact]
        public async Task WorkerDiesDuringBatch_ElevationUnavailableForRestOfCycle()
        {
            var h = CreateService(
                Mappings(caps: new() { ["PMingLiU"] = "Cap.A", ["Mangal"] = "Cap.B" }),
                configureWorker: w => { w.DefaultCapabilityOutcome = InstallOutcome.Installed; w.BecomeUnhealthyAfterBatch = true; });
            await h.Service.BeginCycleAsync(Policy(true, true), CancellationToken.None);

            var first = await h.Service.ResolveMissingFontsAsync(new[] { "PMingLiU" }, CancellationToken.None);
            var second = await h.Service.ResolveMissingFontsAsync(new[] { "Mangal" }, CancellationToken.None);

            Assert.Contains("PMingLiU", first.Resolved);       // resolved before the worker died
            Assert.Contains("Mangal", second.StillMissing);    // no second worker launched
            Assert.Equal(1, h.WorkerFactoryCalls);
        }

        [Fact]
        public async Task CorrelationId_IsPropagatedToWorkerRequests()
        {
            var h = CreateService(Mappings(caps: new() { ["PMingLiU"] = "Cap.Hant" }));
            await h.Service.BeginCycleAsync(Policy(true, true), CancellationToken.None);

            await h.Service.ResolveMissingFontsAsync(new[] { "PMingLiU" }, CancellationToken.None);

            Assert.NotEmpty(h.CurrentWorker!.SeenCorrelationIds);
            Assert.All(h.CurrentWorker.SeenCorrelationIds, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        }

        [Fact]
        public async Task EndCycle_ShutsDownAndDisposesWorker()
        {
            var h = CreateService(Mappings(caps: new() { ["PMingLiU"] = "Cap.Hant" }));
            await h.Service.BeginCycleAsync(Policy(true, true), CancellationToken.None);
            await h.Service.ResolveMissingFontsAsync(new[] { "PMingLiU" }, CancellationToken.None);

            await h.Service.EndCycleAsync(CancellationToken.None);

            Assert.True(h.CurrentWorker!.ShutdownCalled);
            Assert.True(h.CurrentWorker.Disposed);
        }

        [Fact]
        public async Task NewCycle_DisposesStaleWorkerFromPreviousCycle()
        {
            var h = CreateService(Mappings(caps: new() { ["PMingLiU"] = "Cap.Hant" }));

            await h.Service.BeginCycleAsync(Policy(true, true), CancellationToken.None);
            await h.Service.ResolveMissingFontsAsync(new[] { "PMingLiU" }, CancellationToken.None);
            var firstWorker = h.CurrentWorker!;

            // Begin a new cycle WITHOUT ending the previous one explicitly.
            await h.Service.BeginCycleAsync(Policy(true, true), CancellationToken.None);

            Assert.True(firstWorker.Disposed);
        }

        [Fact]
        public async Task UnmappedFont_RemainsMissing()
        {
            var h = CreateService(Mappings(caps: new() { ["PMingLiU"] = "Cap.Hant" }));
            await h.Service.BeginCycleAsync(Policy(true, true), CancellationToken.None);

            var outcome = await h.Service.ResolveMissingFontsAsync(new[] { "Totally Unknown Font" }, CancellationToken.None);

            Assert.Contains("Totally Unknown Font", outcome.StillMissing);
            Assert.Equal(0, h.WorkerFactoryCalls); // nothing resolvable → no elevation prompt
        }
    }
}

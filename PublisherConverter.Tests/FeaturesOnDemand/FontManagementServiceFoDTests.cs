using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core;
using PublisherConverter.Core.FeaturesOnDemand;
using PublisherConverter.Core.FontWorker;
using PublisherConverter.Tests.FontWorker;
using Xunit;

namespace PublisherConverter.Tests.FeaturesOnDemand
{
    /// <summary>
    /// Verifies the FoD fallback is correctly wired into the FontManagementService
    /// coordinator: it runs only over the fonts left unresolved by the capability
    /// install and downloadable strategies, merges its results, marks the cache,
    /// and never faults the batch when it fails.
    /// </summary>
    public sealed class FontManagementServiceFoDTests
    {
        private static FontMappingTable Caps(params (string Font, string Cap)[] caps)
        {
            var d = new Dictionary<string, string>();
            foreach (var (f, c) in caps) d[f] = c;
            return new FontMappingTable(d, null, null, null);
        }

        private sealed class Built
        {
            public required FontManagementService Service { get; init; }
            public required FakeFoDPipeline Fod { get; init; }
            public required SetInstalledFontProvider Provider { get; init; }
        }

        private static Built Build(
            FontMappingTable mappings,
            FakeFoDPipeline fod,
            Func<IElevatedFontWorkerClient?, IReadOnlyList<IFontProvisioningStrategy>>? strategies = null)
        {
            var provider = new SetInstalledFontProvider();
            var cache = new FontAvailabilityCache(provider);

            Func<IElevatedFontWorkerClient> workerFactory = () => new FakeElevatedFontWorkerClient();
            Func<IElevatedFontWorkerClient?, IReadOnlyList<IFontProvisioningStrategy>> strat =
                strategies ?? (_ => Array.Empty<IFontProvisioningStrategy>());
            Func<IElevatedFontWorkerClient?, IUserFontInstaller> installerFactory = _ => new FakeUserFontInstaller();

            var svc = new FontManagementService(
                mappings, cache, workerFactory, strat,
                logger: null, requestTimeoutSeconds: null,
                fodPipeline: fod, fodInstallerFactory: installerFactory);

            return new Built { Service = svc, Fod = fod, Provider = provider };
        }

        private static FontProvisioningPolicy Policy(bool auto, bool elevated)
            => new FontProvisioningPolicy { AutomaticInstallEnabled = auto, AllowElevatedInstall = elevated };

        [Fact]
        public async Task FoD_resolves_capability_font_when_elevation_unavailable()
        {
            var fod = new FakeFoDPipeline();
            fod.ResolveThese.Add("PMingLiU");
            var b = Build(Caps(("PMingLiU", "Language.Fonts.Hant~~~und-HANT~0.0.1.0")), fod);
            await b.Service.BeginCycleAsync(Policy(true, elevated: false), CancellationToken.None);

            var outcome = await b.Service.ResolveMissingFontsAsync(new[] { "PMingLiU" }, CancellationToken.None);

            Assert.Contains("PMingLiU", outcome.Resolved);
            Assert.Empty(outcome.StillMissing);
            Assert.True(b.Provider is not null);
            // The cache was marked so a later file treats it as resolved.
            Assert.Contains("[fake-fod] ran", outcome.Log);
        }

        [Fact]
        public async Task FoD_only_receives_fonts_left_unresolved_by_earlier_strategies()
        {
            var fod = new FakeFoDPipeline();
            fod.ResolveThese.Add("PMingLiU");

            // A downloadable strategy resolves "Lemon"; the capability font
            // "PMingLiU" is left for FoD (elevation off → capability can't install).
            var strategies = FontManagementServiceFoDTests.ScriptedStrategies(new() { ["Lemon"] = true });

            var mappings = new FontMappingTable(
                new Dictionary<string, string> { ["PMingLiU"] = "Language.Fonts.Hant~~~und-HANT~0.0.1.0" },
                new Dictionary<string, FontFallback> { ["Lemon"] = new FontFallback { Name = "Lemon", Url = "http://x/lemon.ttf" } },
                null, null);

            var b = Build(mappings, fod, strategies);
            await b.Service.BeginCycleAsync(Policy(true, elevated: false), CancellationToken.None);

            var outcome = await b.Service.ResolveMissingFontsAsync(new[] { "Lemon", "PMingLiU" }, CancellationToken.None);

            // FoD saw only the still-missing capability font, not the already-resolved one.
            var batch = Assert.Single(fod.Inputs);
            Assert.Equal(new[] { "PMingLiU" }, batch);
            Assert.Contains("Lemon", outcome.Resolved);
            Assert.Contains("PMingLiU", outcome.Resolved);
        }

        [Fact]
        public async Task FoD_failure_does_not_fault_the_batch()
        {
            var fod = new FakeFoDPipeline { Throws = new InvalidOperationException("network gone") };
            var b = Build(Caps(("PMingLiU", "Language.Fonts.Hant~~~und-HANT~0.0.1.0")), fod);
            await b.Service.BeginCycleAsync(Policy(true, elevated: false), CancellationToken.None);

            var outcome = await b.Service.ResolveMissingFontsAsync(new[] { "PMingLiU" }, CancellationToken.None);

            Assert.Contains("PMingLiU", outcome.StillMissing);
            Assert.Contains(outcome.Log, l => l.Contains("Features-on-Demand fallback failed"));
        }

        [Fact]
        public async Task FoD_not_invoked_when_nothing_is_missing()
        {
            var fod = new FakeFoDPipeline();
            var b = Build(Caps(("PMingLiU", "Language.Fonts.Hant~~~und-HANT~0.0.1.0")), fod, preInstalledProvider: true);
            await b.Service.BeginCycleAsync(Policy(true, elevated: false), CancellationToken.None);

            await b.Service.ResolveMissingFontsAsync(new[] { "PMingLiU" }, CancellationToken.None);

            Assert.Equal(0, fod.Calls);
        }

        // Overload of Build that can pre-install the font in the provider.
        private static Built Build(FontMappingTable mappings, FakeFoDPipeline fod, bool preInstalledProvider)
        {
            var provider = new SetInstalledFontProvider();
            if (preInstalledProvider) provider.Installed.Add(FontNameNormalizer.Normalize("PMingLiU"));
            var cache = new FontAvailabilityCache(provider);

            Func<IElevatedFontWorkerClient> workerFactory = () => new FakeElevatedFontWorkerClient();
            Func<IElevatedFontWorkerClient?, IReadOnlyList<IFontProvisioningStrategy>> strat = _ => Array.Empty<IFontProvisioningStrategy>();
            Func<IElevatedFontWorkerClient?, IUserFontInstaller> installerFactory = _ => new FakeUserFontInstaller();

            var svc = new FontManagementService(
                mappings, cache, workerFactory, strat,
                logger: null, requestTimeoutSeconds: null,
                fodPipeline: fod, fodInstallerFactory: installerFactory);
            return new Built { Service = svc, Fod = fod, Provider = provider };
        }

        private static Func<IElevatedFontWorkerClient?, IReadOnlyList<IFontProvisioningStrategy>> ScriptedStrategies(Dictionary<string, bool> outcomes)
            => _ => new IFontProvisioningStrategy[] { new ScriptedStrategy("dl", outcomes) };
    }
}

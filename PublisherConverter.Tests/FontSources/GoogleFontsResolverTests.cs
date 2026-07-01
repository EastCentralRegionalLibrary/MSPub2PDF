using System;
using System.Collections.Generic;
using System.IO;
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
        private const string RobotoBase = "https://raw.githubusercontent.com/google/fonts/main/ofl/roboto/";

        private static string RobotoMetadata()
            => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "FontSources", "roboto-METADATA.pb"));

        private static (GoogleFontsResolver resolver, FakeFontHttpClient http, FontFamilyNormalizer norm) Build(DisambiguationCallback? vfCallback = null)
        {
            var config = FontSourceConfiguration.LoadFromJson(Cfg);
            var http = new FakeFontHttpClient();
            var resolver = new GoogleFontsResolver(config, http, new FontLicenseEvaluator(config.Policy.License), variableFontCallback: vfCallback);
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
        public void ParseMetadataFilenames_reads_variable_filenames_from_captured_roboto_metadata()
        {
            var filenames = GoogleFontsResolver.ParseMetadataFilenames(RobotoMetadata());

            Assert.Equal(new[] { "Roboto[wdth,wght].ttf", "Roboto-Italic[wdth,wght].ttf" }, filenames);
        }

        [Fact]
        public void DeclaredStyleToken_maps_variable_and_static_names()
        {
            Assert.Equal("Regular", GoogleFontsResolver.DeclaredStyleToken("Roboto[wdth,wght].ttf"));
            Assert.Equal("Italic", GoogleFontsResolver.DeclaredStyleToken("Roboto-Italic[wdth,wght].ttf"));
            Assert.Equal("Bold", GoogleFontsResolver.DeclaredStyleToken("Lato-Bold.ttf"));
        }

        [Fact]
        public async Task Variable_only_family_installs_after_user_accepts()
        {
            int prompts = 0;
            string? promptedLabel = null;
            DisambiguationCallback accept = (name, candidates, ct) =>
            {
                prompts++;
                promptedLabel = candidates[0].Slug;
                return Task.FromResult(0);
            };

            var (resolver, http, norm) = Build(accept);
            http.SeedPage(RobotoBase + "METADATA.pb", RobotoMetadata());
            http.SeedFile(RobotoBase + "Roboto%5Bwdth%2Cwght%5D.ttf", TtfTestBuilder.BuildValidTtf("Roboto"));
            http.SeedFile(RobotoBase + "Roboto-Italic%5Bwdth%2Cwght%5D.ttf", TtfTestBuilder.BuildValidTtf("Roboto"));

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Roboto"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved);
            Assert.Equal(ResolutionLayer.GoogleFonts, result.Layer);
            Assert.Equal(1, prompts);
            Assert.Equal("Roboto — variable font", promptedLabel);
            Assert.Equal(2, h.Installer.InstalledFromStream.Count); // upright + italic VF
        }

        [Fact]
        public async Task Variable_only_family_is_skipped_when_user_declines()
        {
            DisambiguationCallback decline = (name, candidates, ct) => Task.FromResult(-1);

            var (resolver, http, norm) = Build(decline);
            http.SeedPage(RobotoBase + "METADATA.pb", RobotoMetadata());
            http.SeedFile(RobotoBase + "Roboto%5Bwdth%2Cwght%5D.ttf", TtfTestBuilder.BuildValidTtf("Roboto"));

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Roboto"), h.Context(), CancellationToken.None);

            Assert.False(result.IsResolved);
            Assert.Equal(AcquisitionStatus.Missing, result.Status);
            Assert.Contains("variable-font", result.FailureReason);
            Assert.Empty(http.Downloaded);
            Assert.Empty(h.Installer.InstalledFromStream);
        }

        [Fact]
        public async Task Variable_only_family_is_skipped_when_no_callback_is_wired()
        {
            var (resolver, http, norm) = Build(vfCallback: null);
            http.SeedPage(RobotoBase + "METADATA.pb", RobotoMetadata());
            http.SeedFile(RobotoBase + "Roboto%5Bwdth%2Cwght%5D.ttf", TtfTestBuilder.BuildValidTtf("Roboto"));

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Roboto"), h.Context(), CancellationToken.None);

            Assert.False(result.IsResolved);
            Assert.Empty(h.Installer.InstalledFromStream); // never installed unprompted
        }

        [Fact]
        public async Task Static_family_never_invokes_the_variable_font_prompt()
        {
            int prompts = 0;
            DisambiguationCallback callback = (name, candidates, ct) => { prompts++; return Task.FromResult(0); };

            var (resolver, http, norm) = Build(callback);
            http.SeedFile(Base + "OpenSans-Regular.ttf", TtfTestBuilder.BuildValidTtf("Open Sans"));

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Open Sans"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved);
            Assert.Equal(0, prompts);
        }

        [Fact]
        public async Task Variable_font_decision_is_remembered_for_the_operation()
        {
            int prompts = 0;
            DisambiguationCallback decline = (name, candidates, ct) => { prompts++; return Task.FromResult(-1); };

            var (resolver, http, norm) = Build(decline);
            http.SeedPage(RobotoBase + "METADATA.pb", RobotoMetadata());

            using var h = new ResolverHarness();
            var context = h.Context();
            await resolver.TryResolveAsync(norm.Parse("Roboto"), context, CancellationToken.None);
            await resolver.TryResolveAsync(norm.Parse("Roboto"), context, CancellationToken.None);

            Assert.Equal(1, prompts); // second attempt reuses the remembered decision
        }

        [Fact]
        public async Task Declared_static_files_from_metadata_install_without_prompt()
        {
            int prompts = 0;
            DisambiguationCallback callback = (name, candidates, ct) => { prompts++; return Task.FromResult(0); };

            var (resolver, http, norm) = Build(callback);
            // No template-shaped static file, but METADATA.pb declares a static
            // (non-variable) filename — install it directly, no prompt.
            http.SeedPage(Base + "METADATA.pb",
                "name: \"Open Sans\"\nfonts {\n  filename: \"OpenSansCustom-Regular.ttf\"\n}\n");
            http.SeedFile(Base + "OpenSansCustom-Regular.ttf", TtfTestBuilder.BuildValidTtf("Open Sans"));

            using var h = new ResolverHarness();
            var result = await resolver.TryResolveAsync(norm.Parse("Open Sans"), h.Context(), CancellationToken.None);

            Assert.True(result.IsResolved);
            Assert.Equal(0, prompts);
            Assert.Single(h.Installer.InstalledFromStream);
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

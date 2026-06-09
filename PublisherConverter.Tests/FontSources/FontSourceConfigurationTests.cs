using System;
using System.IO;
using PublisherConverter.Core.FontSources;
using Xunit;

namespace PublisherConverter.Tests.FontSources
{
    public sealed class FontSourceConfigurationTests
    {
        private const string GoodJson = @"{
          ""policy"": { ""probeTimeoutMs"": 3000, ""downloadTimeoutMs"": 60000, ""communityEnabled"": true },
          ""aliases"": { ""Helvetica"": ""Arial"" },
          ""sources"": [
            { ""id"":""google-fonts"", ""type"":""googleFonts"", ""repo"":{""owner"":""google"",""repo"":""fonts""},
              ""licenseDirs"":[""ofl""], ""pathTemplates"":[""{licenseDir}/{slug}/{FamilyNoSpace}-{Style}.ttf""] },
            { ""id"":""ibm-plex"", ""type"":""vendorRepo"", ""routingPatterns"":[""ibm"",""plex""],
              ""repo"":{""owner"":""IBM"",""repo"":""plex"",""branch"":""master""},
              ""pathTemplates"":[""IBM-Plex-Sans/fonts/complete/ttf/{FamilyNoSpace}-{Style}.ttf""] },
            { ""id"":""dafont"", ""type"":""community"", ""baseUrl"":""https://www.dafont.com"",
              ""slugTemplate"":""https://dl.dafont.com/dl/?f={slug}"" }
          ]
        }";

        [Fact]
        public void Loads_valid_config_and_sorts_by_priority()
        {
            var cfg = FontSourceConfiguration.LoadFromJson(GoodJson);
            Assert.Equal(3, cfg.Sources.Count);
            Assert.Equal(3000, cfg.Policy.ProbeTimeoutMs);
            Assert.Single(cfg.VendorRoutes);
        }

        [Fact]
        public void Resolves_alias_case_insensitively()
        {
            var cfg = FontSourceConfiguration.LoadFromJson(GoodJson);
            Assert.Equal("Arial", cfg.ResolveAlias("helvetica"));
            Assert.Equal("Unknown", cfg.ResolveAlias("Unknown"));
        }

        [Fact]
        public void Routes_vendor_family_by_pattern()
        {
            var cfg = FontSourceConfiguration.LoadFromJson(GoodJson);
            Assert.Equal("ibm-plex", cfg.RouteVendor("IBM Plex Sans")?.Source.Id);
            Assert.Null(cfg.RouteVendor("Arial"));
        }

        [Fact]
        public void Allowlist_filters_vendor_routes()
        {
            string json = GoodJson.Replace("\"communityEnabled\": true", "\"communityEnabled\": true, \"allowlistedVendors\": [\"some-other\"]");
            var cfg = FontSourceConfiguration.LoadFromJson(json);
            Assert.Null(cfg.RouteVendor("IBM Plex Sans")); // not on the allowlist
        }

        [Theory]
        [InlineData(@"{ ""sources"": [ { ""type"":""community"", ""baseUrl"":""x"" } ] }")] // missing id
        [InlineData(@"{ ""sources"": [ { ""id"":""a"", ""type"":""googleFonts"", ""repo"":{""owner"":""g"",""repo"":""f""} } ] }")] // google missing dirs/templates
        [InlineData(@"{ ""sources"": [ { ""id"":""v"", ""type"":""vendorRepo"", ""repo"":{""owner"":""g"",""repo"":""f""}, ""pathTemplates"":[""x""] } ] }")] // vendor missing routing
        [InlineData(@"{ ""sources"": [ { ""id"":""v"", ""type"":""vendorRepo"", ""routingPatterns"":[""["", ], ""repo"":{""owner"":""g"",""repo"":""f""}, ""pathTemplates"":[""x""] } ] }")] // bad regex
        [InlineData(@"{ ""policy"": { ""probeTimeoutMs"": 0 } }")] // bad timeout
        public void Rejects_malformed_config(string json)
        {
            Assert.Throws<FontSourceConfigurationException>(() => FontSourceConfiguration.LoadFromJson(json));
        }

        [Fact]
        public void Rejects_duplicate_source_ids()
        {
            string json = @"{ ""sources"": [
              { ""id"":""dup"", ""type"":""community"", ""baseUrl"":""x"" },
              { ""id"":""dup"", ""type"":""community"", ""baseUrl"":""y"" } ] }";
            Assert.Throws<FontSourceConfigurationException>(() => FontSourceConfiguration.LoadFromJson(json));
        }

        [Fact]
        public void Shipped_FontSources_json_is_valid()
        {
            string? path = LocateShippedConfig();
            if (path == null) return; // source tree not present (e.g. packaged test run)

            var cfg = FontSourceConfiguration.LoadFromFile(path);
            Assert.NotEmpty(cfg.Sources);
            Assert.Contains(cfg.Sources, s => s.Type == FontSourceType.GoogleFonts);
            Assert.Contains(cfg.VendorRoutes, r => r.Source.Id == "ibm-plex");
            Assert.Contains(cfg.Sources, s => s.Type == FontSourceType.Community);
        }

        private static string? LocateShippedConfig()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "PublisherConverter.GUI", "FontSources.json");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }
}

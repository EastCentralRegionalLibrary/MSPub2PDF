using System.Linq;
using PublisherConverter.Core;
using PublisherConverter.Core.FeaturesOnDemand;
using Xunit;

namespace PublisherConverter.Tests.FeaturesOnDemand
{
    public sealed class FontLanguageResolverTests
    {
        private static FontMappingTable Mapping() => FontMappingLoader.LoadFromJson(@"{
            ""windowsCapabilities"": {
                ""PMingLiU"": ""Language.Fonts.Hant~~~und-HANT~0.0.1.0"",
                ""Meiryo"":   ""Language.Fonts.Jpan~~~und-JPAN~0.0.1.0"",
                ""MingLiU"":  ""Language.Fonts.Hant~~~und-HANT~0.0.1.0"",
                ""Euphemia"": ""Language.Fonts.Cans~~~und-CANS~0.0.1.0""
            }
        }");

        [Theory]
        [InlineData("Language.Fonts.Hant~~~und-HANT~0.0.1.0", "Hant")]
        [InlineData("Language.Fonts.Jpan~~~und-JPAN~0.0.1.0", "Jpan")]
        [InlineData("Language.Fonts.Thai~~~und-THAI~0.0.1.0", "Thai")]
        [InlineData("Language.Fonts.Cans~~~und-CANS~0.0.1.0", "Cans")]
        public void ParseScriptToken_extracts_script(string capability, string expected)
        {
            Assert.Equal(expected, FontLanguageResolver.ParseScriptToken(capability));
        }

        [Theory]
        [InlineData("not-a-capability")]
        [InlineData("")]
        [InlineData(null)]
        public void ParseScriptToken_returns_null_for_non_capability(string? capability)
        {
            Assert.Null(FontLanguageResolver.ParseScriptToken(capability));
        }

        [Fact]
        public void TryGetLanguage_resolves_via_capability_map()
        {
            var resolver = new FontLanguageResolver(Mapping());

            Assert.True(resolver.TryGetLanguage("PMingLiU", out var lang));
            Assert.Equal("Hant", lang);

            Assert.True(resolver.TryGetLanguage("Meiryo", out lang));
            Assert.Equal("Jpan", lang);
        }

        [Fact]
        public void TryGetLanguage_is_false_for_unmapped_font()
        {
            var resolver = new FontLanguageResolver(Mapping());
            Assert.False(resolver.TryGetLanguage("Totally Unknown", out _));
        }

        [Fact]
        public void GroupByLanguage_groups_and_drops_unmapped()
        {
            var resolver = new FontLanguageResolver(Mapping());

            var grouped = resolver.GroupByLanguage(new[] { "PMingLiU", "MingLiU", "Meiryo", "Unknown Font" });

            Assert.Equal(2, grouped.Count);
            Assert.Equal(new[] { "PMingLiU", "MingLiU" }, grouped["Hant"]);
            Assert.Equal(new[] { "Meiryo" }, grouped["Jpan"]);
            Assert.DoesNotContain(grouped.Values.SelectMany(v => v), f => f == "Unknown Font");
        }
    }
}

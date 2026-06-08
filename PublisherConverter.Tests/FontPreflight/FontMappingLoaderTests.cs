using System.IO;
using PublisherConverter.Core;
using Xunit;

namespace PublisherConverter.Tests
{
    public sealed class FontMappingLoaderTests
    {
        [Fact]
        public void Missing_file_returns_empty_table_without_throwing()
        {
            var table = FontMappingLoader.LoadFromFile("/nonexistent/path/FontMapping.json");
            Assert.Equal(0, table.EntryCount);
        }

        [Fact]
        public void Malformed_json_returns_empty_table_without_throwing()
        {
            var table = FontMappingLoader.LoadFromJson("{ this is not json");
            Assert.Equal(0, table.EntryCount);
        }

        [Fact]
        public void Loads_capabilities_and_fallbacks_with_normalized_lookup()
        {
            const string json = @"{
                ""windowsCapabilities"": {
                    ""PMingLiU"": ""Language.Fonts.Hant~~~und-HANT0.0.1.0"",
                    ""Mangal"":   ""Language.Fonts.Deva~~~und-DEVA0.0.1.0""
                },
                ""externalFallbacks"": {
                    ""Batang"": { ""name"": ""Noto Serif KR"", ""url"": ""https://example/NotoSerifKR.ttf"" }
                }
            }";

            var table = FontMappingLoader.LoadFromJson(json);

            // Lookups are normalized (whitespace + casing).
            Assert.True(table.TryGetCapability("pmingliu", out var cap));
            Assert.Equal("Language.Fonts.Hant~~~und-HANT0.0.1.0", cap);
            Assert.True(table.TryGetCapability("  P Ming Liu  ", out _));

            Assert.True(table.TryGetFallback("BATANG", out var fb));
            Assert.Equal("Noto Serif KR", fb.Name);
            Assert.Equal("https://example/NotoSerifKR.ttf", fb.Url);

            Assert.False(table.TryGetCapability("Helvetica", out _));
            Assert.False(table.TryGetFallback("Helvetica", out _));

            Assert.True(table.HasAnyMappingFor("mangal"));
            Assert.True(table.HasAnyMappingFor("PMingLiU"));
            Assert.True(table.HasAnyMappingFor("Batang"));
        }

        [Fact]
        public void Entries_with_blank_url_are_dropped()
        {
            const string json = @"{
                ""externalFallbacks"": {
                    ""Foo"": { ""name"": ""Foo Sans"", ""url"": """" },
                    ""Bar"": { ""name"": ""Bar Sans"", ""url"": ""https://example/bar.ttf"" }
                }
            }";

            var table = FontMappingLoader.LoadFromJson(json);

            Assert.False(table.TryGetFallback("Foo", out _));
            Assert.True(table.TryGetFallback("Bar", out _));
        }
    }
}

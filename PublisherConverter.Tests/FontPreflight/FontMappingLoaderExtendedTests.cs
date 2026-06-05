using PublisherConverter.Core;
using Xunit;

namespace PublisherConverter.Tests
{
    public sealed class FontMappingLoaderExtendedTests
    {
        [Fact]
        public void Loads_googleFonts_and_githubFonts_sections()
        {
            const string json = @"{
                ""googleFonts"": {
                    ""Roboto"": {},
                    ""Source Sans Pro"": { ""family"": ""Source Sans Pro"" }
                },
                ""githubFonts"": {
                    ""Custom Raw"": {
                        ""owner"": ""owner1"",
                        ""repo"": ""repo1"",
                        ""path"": ""fonts/Custom-Regular.ttf"",
                        ""ref"": ""main""
                    },
                    ""Custom Release"": {
                        ""owner"": ""owner2"",
                        ""repo"": ""repo2"",
                        ""release"": ""v1.0"",
                        ""assetPattern"": ""*.zip""
                    }
                }
            }";

            var table = FontMappingLoader.LoadFromJson(json);

            Assert.True(table.TryGetGoogleFont("roboto", out var google1));
            Assert.Null(google1.Family);

            Assert.True(table.TryGetGoogleFont("source sans pro", out var google2));
            Assert.Equal("Source Sans Pro", google2.Family);

            Assert.True(table.TryGetGitHubFont("custom raw", out var gh1));
            Assert.Equal("owner1", gh1.Owner);
            Assert.Equal("repo1", gh1.Repo);
            Assert.Equal("fonts/Custom-Regular.ttf", gh1.Path);
            Assert.Equal("main", gh1.Ref);

            Assert.True(table.TryGetGitHubFont("CUSTOM RELEASE", out var gh2));
            Assert.Equal("v1.0", gh2.Release);
            Assert.Equal("*.zip", gh2.AssetPattern);
        }

        [Fact]
        public void Drops_github_entries_missing_owner_or_repo()
        {
            const string json = @"{
                ""githubFonts"": {
                    ""No Owner"": { ""repo"": ""r"", ""path"": ""x.ttf"" },
                    ""No Repo"":  { ""owner"": ""o"", ""path"": ""x.ttf"" },
                    ""Valid"":    { ""owner"": ""o"", ""repo"": ""r"", ""path"": ""x.ttf"" }
                }
            }";

            var table = FontMappingLoader.LoadFromJson(json);

            Assert.False(table.TryGetGitHubFont("No Owner", out _));
            Assert.False(table.TryGetGitHubFont("No Repo", out _));
            Assert.True(table.TryGetGitHubFont("Valid", out _));
        }

        [Fact]
        public void All_sections_normalize_lookup()
        {
            const string json = @"{
                ""windowsCapabilities"": { ""PMingLiU"": ""cap"" },
                ""googleFonts"":         { ""Roboto"": {} },
                ""githubFonts"":         { ""Inter"":  { ""owner"":""o"", ""repo"":""r"", ""path"":""x.ttf"" } },
                ""externalFallbacks"":   { ""Foo"":    { ""url"": ""https://example/x.ttf"" } }
            }";

            var table = FontMappingLoader.LoadFromJson(json);

            Assert.True(table.TryGetCapability("p ming liu", out _));
            Assert.True(table.TryGetGoogleFont("ROBOTO", out _));
            Assert.True(table.TryGetGitHubFont("  inter  ", out _));
            Assert.True(table.TryGetFallback("FOO", out _));
        }

        [Fact]
        public void HasAnyMappingFor_inspects_every_section()
        {
            const string json = @"{
                ""googleFonts"": { ""GFonly"": {} },
                ""githubFonts"": { ""GHonly"": { ""owner"":""o"", ""repo"":""r"", ""path"":""x.ttf"" } }
            }";

            var table = FontMappingLoader.LoadFromJson(json);

            Assert.True(table.HasAnyMappingFor("GFonly"));
            Assert.True(table.HasAnyMappingFor("GHonly"));
            Assert.False(table.HasAnyMappingFor("UnknownFont"));
        }
    }
}

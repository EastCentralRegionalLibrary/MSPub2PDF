using PublisherConverter.Core.FeaturesOnDemand;
using Xunit;

namespace PublisherConverter.Tests.FeaturesOnDemand
{
    public sealed class FontFileInspectorTests
    {
        [Theory]
        [InlineData("font.ttf", true)]
        [InlineData("font.ttc", true)]
        [InlineData("font.otf", true)]
        [InlineData("font.TTF", true)]
        [InlineData("readme.txt", false)]
        [InlineData("font.woff2", false)]
        public void IsFontFile_recognises_font_extensions(string name, bool expected)
        {
            Assert.Equal(expected, FontFileInspector.IsFontFile(name));
        }

        [Theory]
        [InlineData("font.ttc", true)]
        [InlineData("font.ttf", false)]
        public void IsCollection_only_true_for_ttc(string name, bool expected)
        {
            Assert.Equal(expected, FontFileInspector.IsCollection(name));
        }

        [Fact]
        public void GetFamilyNames_reads_single_family_from_ttf()
        {
            byte[] ttf = FontBuilder.BuildTtf("Leelawadee UI");

            var families = FontFileInspector.GetFamilyNames(ttf);

            Assert.Equal(new[] { "Leelawadee UI" }, families);
        }

        [Fact]
        public void GetFamilyNames_reads_all_families_from_ttc()
        {
            byte[] ttc = FontBuilder.BuildTtc("PMingLiU", "MingLiU", "MingLiU_HKSCS");

            var families = FontFileInspector.GetFamilyNames(ttc);

            Assert.Equal(new[] { "PMingLiU", "MingLiU", "MingLiU_HKSCS" }, families);
        }

        [Fact]
        public void GetFamilyNames_is_empty_for_garbage()
        {
            Assert.Empty(FontFileInspector.GetFamilyNames(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 }));
        }
    }
}

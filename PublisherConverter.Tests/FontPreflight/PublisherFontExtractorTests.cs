using PublisherConverter.Core;
using Xunit;

namespace PublisherConverter.Tests
{
    // Cross-platform (Linux CI included): the extractor and surrounding helpers
    // are pure managed code, and the OS-specific installed-font lookup is
    // replaced by a fake in the auditor tests.
    //
    // What this fixture exercises (and why it's a good one): TestFile.pub is
    // template-derived, so it contains a paragraph-STYLE table and page/section
    // -name records that share the font records' byte shapes — the exact things
    // that broke the first-pass parser. Real fonts: Calibri, Cambria,
    // Century Gothic, New York, Lemon Cookie Bold, Symbol (+ the OS fallback
    // block).
    public sealed class PublisherFontExtractorTests
    {
        private const string Sample = "TestData/TestFile.pub";

        [Fact]
        public void Extracts_real_fonts_from_both_streams()
        {
            var fonts = new PublisherFontExtractor().ExtractFontNames(Sample);

            Assert.Contains("Lemon Cookie Bold", fonts);
            Assert.Contains("New York", fonts);
            Assert.Contains("Century Gothic", fonts);
            Assert.Contains("Calibri", fonts);
        }

        [Fact]
        public void Does_not_mistake_style_or_page_names_for_fonts()
        {
            var fonts = new PublisherFontExtractor().ExtractFontNames(Sample);

            // Paragraph styles (Quill style table) — must NOT be picked up as fonts.
            Assert.DoesNotContain("Recipe Title", fonts);
            Assert.DoesNotContain("Cookbook Title", fonts);
            Assert.DoesNotContain("Family Name", fonts);
            // Page / section names (Contents 0F C0 records) — must NOT be picked up as fonts.
            Assert.DoesNotContain("Cover Page", fonts);
            Assert.DoesNotContain("Contents Page", fonts);
            Assert.DoesNotContain("Recipe Card", fonts);
        }

        [Fact]
        public void Returns_empty_set_for_non_ole_file()
        {
            string fakePath = System.IO.Path.GetTempFileName();
            System.IO.File.WriteAllText(fakePath, "this is not a .pub");
            try
            {
                var fonts = new PublisherFontExtractor().ExtractFontNames(fakePath);
                Assert.Empty(fonts);
            }
            finally
            {
                System.IO.File.Delete(fakePath);
            }
        }
    }
}

using System.Linq;
using PublisherConverter.Core.FontSources;
using Xunit;

namespace PublisherConverter.Tests.FontSources
{
    public sealed class FontFamilyNormalizerTests
    {
        private static FontFamilyNormalizer New(System.Func<string, string>? alias = null)
            => new FontFamilyNormalizer(styleSuffixes: null, aliasResolver: alias);

        [Fact]
        public void Strips_trailing_bold_style()
        {
            var r = New().Parse("Open Sans Bold");
            Assert.Equal("Open Sans", r.NormalizedFamily);
            Assert.Equal(new[] { "Bold" }, r.RequestedStyles);
            Assert.Equal("Bold", FontFamilyNormalizer.StyleFileToken(r.RequestedStyles));
        }

        [Fact]
        public void Strips_bold_and_italic_in_order()
        {
            var r = New().Parse("Times New Roman Bold Italic");
            Assert.Equal("Times New Roman", r.NormalizedFamily);
            Assert.Equal(new[] { "Bold", "Italic" }, r.RequestedStyles);
            Assert.Equal("BoldItalic", FontFamilyNormalizer.StyleFileToken(r.RequestedStyles));
        }

        [Fact]
        public void Decomposes_combined_hyphenated_style()
        {
            var r = New().Parse("Roboto-BoldItalic");
            Assert.Equal("Roboto", r.NormalizedFamily);
            Assert.Equal(new[] { "Bold", "Italic" }, r.RequestedStyles);
        }

        [Fact]
        public void Regular_request_has_no_styles()
        {
            var r = New().Parse("Roboto");
            Assert.Equal("Roboto", r.NormalizedFamily);
            Assert.Empty(r.RequestedStyles);
            Assert.Equal("Regular", FontFamilyNormalizer.StyleFileToken(r.RequestedStyles));
        }

        [Fact]
        public void Does_not_strip_family_to_empty()
        {
            // A font literally named after a style keeps its name.
            var r = New().Parse("Black");
            Assert.Equal("Black", r.NormalizedFamily);
            Assert.Empty(r.RequestedStyles);
        }

        [Fact]
        public void Collapses_repeated_whitespace()
        {
            var r = New().Parse("Open    Sans   Bold");
            Assert.Equal("Open Sans", r.NormalizedFamily);
        }

        [Fact]
        public void Applies_alias_after_stripping()
        {
            var norm = New(alias: f => f == "Source Sans" ? "Source Sans 3" : f);
            var r = norm.Parse("Source Sans Bold");
            Assert.Equal("Source Sans 3", r.NormalizedFamily);
            Assert.Equal(new[] { "Bold" }, r.RequestedStyles);
        }

        [Fact]
        public void Does_not_strip_width_token_condensed_by_default()
        {
            var r = New().Parse("Roboto Condensed");
            Assert.Equal("Roboto Condensed", r.NormalizedFamily);
        }

        [Fact]
        public void Named_weight_with_italic_builds_token()
        {
            var r = New().Parse("Roboto Medium Italic");
            Assert.Equal("Roboto", r.NormalizedFamily);
            Assert.Equal("MediumItalic", FontFamilyNormalizer.StyleFileToken(r.RequestedStyles));
        }

        [Fact]
        public void Splits_run_together_style_without_delimiters()
        {
            var r = New().Parse("OpenSansBold");
            Assert.Equal("OpenSans", r.NormalizedFamily);
            Assert.Equal(new[] { "Bold" }, r.RequestedStyles);
        }

        [Fact]
        public void Decomposes_run_together_compound_styles()
        {
            var r = New().Parse("UbuntuExtraBoldItalic");
            Assert.Equal("Ubuntu", r.NormalizedFamily);
            Assert.Equal(new[] { "ExtraBold", "Italic" }, r.RequestedStyles);
        }

        [Fact]
        public void Applies_alias_to_cleanly_split_run_together_families()
        {
            var norm = New(alias: f => f == "SourceSansPro" ? "Source Sans 3" : f);
            var r = norm.Parse("SourceSansProBold");
            Assert.Equal("Source Sans 3", r.NormalizedFamily);
            Assert.Equal(new[] { "Bold" }, r.RequestedStyles);
        }

        // ---- compound multi-word weights ("Extra Bold" → ExtraBold) ----

        [Theory]
        [InlineData("Montserrat Extra Bold", "Montserrat", "ExtraBold")]
        [InlineData("Montserrat Semi Bold", "Montserrat", "SemiBold")]
        [InlineData("Open Sans Extra Bold", "Open Sans", "ExtraBold")]
        [InlineData("Consolas Extra Bold", "Consolas", "ExtraBold")]
        [InlineData("Montserrat Extra Light", "Montserrat", "ExtraLight")]
        public void Joins_multi_word_weight_into_canonical_style(string requested, string family, string style)
        {
            var r = New().Parse(requested);
            Assert.Equal(family, r.NormalizedFamily);
            Assert.Equal(new[] { style }, r.RequestedStyles);
        }

        [Fact]
        public void Joins_multi_word_weight_followed_by_italic()
        {
            var r = New().Parse("Montserrat Extra Bold Italic");
            Assert.Equal("Montserrat", r.NormalizedFamily);
            Assert.Equal(new[] { "ExtraBold", "Italic" }, r.RequestedStyles);
            Assert.Equal("ExtraBoldItalic", FontFamilyNormalizer.StyleFileToken(r.RequestedStyles));
        }

        [Fact]
        public void Multi_word_weights_parse_with_the_shipped_style_suffix_config()
        {
            // The exact production repro: the real FontSources.json style list.
            var config = FontSourceConfiguration.LoadFromFile(
                System.IO.Path.Combine(System.AppContext.BaseDirectory, "TestData", "FontSources", "FontSources.json"));
            var r = new FontFamilyNormalizer(config).Parse("Montserrat Extra Bold");

            Assert.Equal("Montserrat", r.NormalizedFamily);
            Assert.Equal(new[] { "ExtraBold" }, r.RequestedStyles);
        }

        [Theory]
        [InlineData("Montserrat ExtraBold", "Montserrat", "ExtraBold")] // concatenated form unchanged
        [InlineData("Arial Bold", "Arial", "Bold")]
        public void Single_token_weights_are_unchanged(string requested, string family, string style)
        {
            var r = New().Parse(requested);
            Assert.Equal(family, r.NormalizedFamily);
            Assert.Equal(new[] { style }, r.RequestedStyles);
        }

        [Theory]
        [InlineData("Times New Roman")]
        [InlineData("Franklin Gothic Book")] // "Book" is deliberately not a weight token
        [InlineData("Lucida Sans")]
        public void Family_words_are_never_consumed_as_modifiers(string requested)
        {
            var r = New().Parse(requested);
            Assert.Equal(requested, r.NormalizedFamily);
            Assert.Empty(r.RequestedStyles);
        }

        [Fact]
        public void Font_literally_named_extra_bold_keeps_its_family()
        {
            var r = New().Parse("Extra Bold");
            Assert.Equal("Extra Bold", r.NormalizedFamily);
            Assert.Empty(r.RequestedStyles);
        }

        [Fact]
        public void Modifier_without_base_weight_stays_in_the_family()
        {
            // "Extra" not followed (rightward) by a base weight is family text.
            var r = New().Parse("Extra Swift Italic");
            Assert.Equal("Extra Swift", r.NormalizedFamily);
            Assert.Equal(new[] { "Italic" }, r.RequestedStyles);
        }
    }
}

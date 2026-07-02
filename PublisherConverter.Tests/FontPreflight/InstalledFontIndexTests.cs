using PublisherConverter.Core;
using PublisherConverter.Core.FontSources;
using Xunit;

namespace PublisherConverter.Tests.FontPreflight
{
    // Drives the platform-neutral matcher behind WindowsRegistryFontProvider
    // with in-memory face-name lists — no registry access, runs on Linux CI.
    public sealed class InstalledFontIndexTests
    {
        private static InstalledFontIndex Index(params string[] faces)
            => new InstalledFontIndex(faces, new FontFamilyNormalizer());

        [Fact]
        public void Bare_family_matches_a_large_family_registered_only_per_face()
        {
            // Lucida Sans registers per-face with NO bare "Lucida Sans" entry.
            var index = Index("Lucida Sans Regular", "Lucida Sans Bold", "Lucida Sans Demibold", "Lucida Sans Bold Italic");

            Assert.True(index.IsInstalled("Lucida Sans"));
        }

        [Fact]
        public void Bare_family_is_satisfied_by_any_face_even_without_a_regular()
        {
            // Documented decision: presence of any face counts for a bare
            // family request, even when no true regular is registered.
            var index = Index("Lucida Sans Demibold");

            Assert.True(index.IsInstalled("Lucida Sans"));
        }

        [Fact]
        public void Styled_request_never_falls_back_to_the_family()
        {
            var index = Index("Consolas Italic");

            // Exact miss + request carries a style → genuinely missing (must
            // trigger acquisition), even though the family has a face installed.
            Assert.False(index.IsInstalled("Consolas Extra Bold"));
            Assert.False(index.IsInstalled("Consolas Bold"));

            // The bare family request still matches via the fallback.
            Assert.True(index.IsInstalled("Consolas"));
        }

        [Fact]
        public void Face_per_family_fonts_match_exactly_and_are_never_collapsed()
        {
            var index = Index("Franklin Gothic Book", "Franklin Gothic Medium");

            Assert.True(index.IsInstalled("Franklin Gothic Book"));   // exact hit
            Assert.True(index.IsInstalled("Franklin Gothic Medium")); // exact hit
            Assert.False(index.IsInstalled("Franklin Gothic Demi"));  // different face, not installed
        }

        [Fact]
        public void Face_named_with_a_weight_word_matches_its_exact_name()
        {
            var index = Index("Bahnschrift SemiBold");

            Assert.True(index.IsInstalled("Bahnschrift SemiBold"));
        }

        [Fact]
        public void Simple_family_with_bare_entry_still_works()
        {
            var index = Index("Calibri", "Calibri Bold");

            Assert.True(index.IsInstalled("Calibri"));
            Assert.True(index.IsInstalled("Calibri Bold")); // exact face hit
        }

        [Fact]
        public void Compound_multi_word_weight_requests_are_style_gated_too()
        {
            // Depends on Part 1: "Extra Bold" must parse as a style, not as
            // family text, for the gate to fire.
            var index = Index("Montserrat Italic");

            Assert.False(index.IsInstalled("Montserrat Extra Bold"));
            Assert.True(index.IsInstalled("Montserrat"));
        }

        [Fact]
        public void Styled_request_matches_when_the_exact_face_is_installed()
        {
            var index = Index("Montserrat ExtraBold");

            // Whitespace/hyphen-insensitive exact match: the two-word request
            // normalizes to the installed concatenated face name.
            Assert.True(index.IsInstalled("Montserrat Extra Bold"));
        }

        [Fact]
        public void Unknown_family_is_missing()
        {
            var index = Index("Calibri");

            Assert.False(index.IsInstalled("Comic Sans MS"));
            Assert.False(index.IsInstalled(""));
        }

        [Fact]
        public void Normalization_is_whitespace_hyphen_and_case_insensitive()
        {
            var index = Index("Lucida Sans Regular");

            Assert.True(index.IsInstalled("lucida-sans"));
            Assert.True(index.IsInstalled("LUCIDA  SANS REGULAR"));
        }
    }
}

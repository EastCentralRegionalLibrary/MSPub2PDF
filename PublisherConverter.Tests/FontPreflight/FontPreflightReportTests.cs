using System.Linq;
using PublisherConverter.Core;
using Xunit;

namespace PublisherConverter.Tests
{
    public sealed class FontPreflightReportTests
    {
        [Fact]
        public void Empty_report_has_no_findings_and_emits_no_summary()
        {
            var report = new FontPreflightReport();

            Assert.False(report.HasFindings);
            Assert.Equal(0, report.DistinctMissingFontCount);

            var lines = report.EnumerateSummaryLines().ToList();
            Assert.Single(lines);
            Assert.Contains("no missing fonts", lines[0]);
        }

        [Fact]
        public void Records_per_font_file_lists_and_dedupes_within_a_font()
        {
            var report = new FontPreflightReport();

            report.RecordMissingFonts("C:\\docs\\one.pub",   new[] { "Lemon Cookie Bold", "New York" });
            report.RecordMissingFonts("C:\\docs\\two.pub",   new[] { "New York" });
            // A duplicate (same file + same font) shouldn't show up twice.
            report.RecordMissingFonts("C:\\docs\\one.pub",   new[] { "Lemon Cookie Bold" });

            Assert.True(report.HasFindings);
            Assert.Equal(2, report.DistinctMissingFontCount);

            var snapshot = report.FilesByMissingFont;
            Assert.Equal(new[] { "C:\\docs\\one.pub" }, snapshot["Lemon Cookie Bold"]);
            Assert.Equal(new[] { "C:\\docs\\one.pub", "C:\\docs\\two.pub" }, snapshot["New York"]);
        }

        [Fact]
        public void Summary_lines_are_alphabetised_and_include_each_path()
        {
            var report = new FontPreflightReport();

            report.RecordMissingFonts("/repo/zeta.pub",  new[] { "Zapfino" });
            report.RecordMissingFonts("/repo/alpha.pub", new[] { "Anaheim" });
            report.RecordMissingFonts("/repo/beta.pub",  new[] { "Anaheim" });

            var lines = report.EnumerateSummaryLines().ToList();

            int anaheimHeader = lines.FindIndex(l => l.Contains("Anaheim"));
            int zapfinoHeader = lines.FindIndex(l => l.Contains("Zapfino"));
            Assert.True(anaheimHeader < zapfinoHeader, "Missing-font sections must be alphabetised.");

            Assert.Contains(lines, l => l.Contains("/repo/alpha.pub"));
            Assert.Contains(lines, l => l.Contains("/repo/beta.pub"));
            Assert.Contains(lines, l => l.Contains("/repo/zeta.pub"));
        }
    }
}

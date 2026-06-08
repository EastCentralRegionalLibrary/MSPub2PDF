using System.Collections.Generic;
using PublisherConverter.Core;
using Xunit;

namespace PublisherConverter.Tests
{
    public sealed class FontAuditorTests
    {
        private const string Sample = "TestData/TestFile.pub";

        // Fake provider: stock Windows + Office fonts are "installed"; New York
        // and Lemon Cookie Bold are NOT — they are the expected "missing" set.
        private sealed class StockFontsProvider : IInstalledFontProvider
        {
            private static readonly HashSet<string> Stock = BuildStock();
            public int Calls { get; private set; }

            public bool IsInstalled(string family)
            {
                Calls++;
                return Stock.Contains(FontNameNormalizer.Normalize(family));
            }

            private static HashSet<string> BuildStock()
            {
                var names = new[]
                {
                    "Calibri", "Cambria", "Times New Roman", "Symbol", "Century Gothic", "Arial",
                    "Tahoma", "Sylfaen", "Mangal", "Vrinda", "Raavi", "Shruti", "Kalinga", "Latha",
                    "Gautami", "Tunga", "Kartika", "BrowalliaUPC", "DokChampa", "Microsoft Himalaya",
                    "Malgun Gothic", "MS Mincho", "PMingLiU", "SimSun", "Estrangelo Edessa", "MV Boli",
                    "Iskoola Pota", "Nyala", "Plantagenet Cherokee", "Euphemia Regular CAS", "DaunPenh",
                    "Mongolian Baiti", "EucrosiaUPC", "Batang", "Angsana New",
                };
                var set = new HashSet<string>();
                foreach (var n in names) set.Add(FontNameNormalizer.Normalize(n));
                return set;
            }
        }

        [Fact]
        public void Reports_exactly_the_missing_fonts_alphabetized()
        {
            var auditor = new FontAuditor(
                new PublisherFontExtractor(),
                new FontAvailabilityCache(new StockFontsProvider()));

            var result = new RenderResult();
            auditor.AuditFonts(Sample, result);

            Assert.Equal(2, result.MissingFontsCount);
            Assert.Equal("Lemon Cookie Bold | New York", result.MissingFontsList);
        }

        [Fact]
        public void ResolveMissingFonts_returns_alphabetized_list()
        {
            var auditor = new FontAuditor(
                new PublisherFontExtractor(),
                new FontAvailabilityCache(new StockFontsProvider()));

            var missing = auditor.ResolveMissingFonts(Sample);

            Assert.Equal(new[] { "Lemon Cookie Bold", "New York" }, missing);
        }

        [Fact]
        public void Cache_resolves_each_distinct_font_only_once()
        {
            var provider = new StockFontsProvider();
            var cache = new FontAvailabilityCache(provider);

            cache.IsInstalled("Calibri");
            cache.IsInstalled("calibri");
            cache.IsInstalled("CALIBRI ");

            Assert.Equal(1, provider.Calls);
        }

        [Fact]
        public void NoOpFontAuditor_reports_no_missing_fonts()
        {
            var auditor = new NoOpFontAuditor();
            var result = new RenderResult();
            auditor.AuditFonts(Sample, result);

            Assert.Equal(0, result.MissingFontsCount);
            Assert.Equal("None", result.MissingFontsList);
            Assert.Empty(auditor.ResolveMissingFonts(Sample));
        }
    }
}

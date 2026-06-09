using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FeaturesOnDemand;
using Xunit;

namespace PublisherConverter.Tests.FeaturesOnDemand
{
    public sealed class ManagedCabFontExtractorTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "fod-extract-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public async Task Extracts_only_font_files_and_parses_families()
        {
            byte[] ttf = FontBuilder.BuildTtf("Nirmala UI");
            byte[] ttc = FontBuilder.BuildTtc("PMingLiU", "MingLiU");
            byte[] readme = Encoding.ASCII.GetBytes("not a font");
            byte[] cab = CabBuilder.Build(new[]
            {
                ("Nirmala.ttf", ttf),
                ("mingliu.ttc", ttc),
                ("readme.txt", readme),
            }, CabCompression.None);

            string cabPath = Path.Combine(_dir, "in.cab");
            Directory.CreateDirectory(_dir);
            await File.WriteAllBytesAsync(cabPath, cab);

            var extractor = new ManagedCabFontExtractor();

            // Enumerate sees everything; extraction keeps only fonts.
            Assert.Equal(3, extractor.Enumerate(cabPath).Count);

            string outDir = Path.Combine(_dir, "out");
            var fonts = await extractor.ExtractFontsAsync(cabPath, outDir, CancellationToken.None);

            Assert.Equal(2, fonts.Count);
            Assert.DoesNotContain(fonts, f => f.FileName == "readme.txt");
            Assert.False(File.Exists(Path.Combine(outDir, "readme.txt")));

            var ttcFont = fonts.Single(f => f.FileName == "mingliu.ttc");
            Assert.True(ttcFont.IsCollection);
            Assert.Equal(new[] { "PMingLiU", "MingLiU" }, ttcFont.FamilyNames);

            var ttfFont = fonts.Single(f => f.FileName == "Nirmala.ttf");
            Assert.False(ttfFont.IsCollection);
            Assert.Equal(new[] { "Nirmala UI" }, ttfFont.FamilyNames);
            Assert.True(File.Exists(ttfFont.FilePath));
        }
    }
}

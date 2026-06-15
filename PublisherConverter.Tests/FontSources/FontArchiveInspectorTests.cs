using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FontSources;
using Xunit;

namespace PublisherConverter.Tests.FontSources
{
    public sealed class FontArchiveInspectorTests
    {
        [Fact]
        public async Task Extracts_only_ttf_and_reads_license()
        {
            byte[] zip = ZipBuilder.Build(new[]
            {
                ("MyFont-Regular.ttf", TtfTestBuilder.BuildValidTtf("My Font")),
                ("preview.png", new byte[] { 1, 2, 3, 4 }),
                ("MyFont.otf", new byte[] { 9, 9, 9 }),
                ("readme.txt", ZipBuilder.Text("This font is free for personal use only.")),
            });

            var inspector = new FontArchiveInspector();
            var hints = new ArchiveHints { ExtractExtensions = { }, LicenseFileNames = { } };
            hints.ExtractExtensions.Clear(); hints.ExtractExtensions.Add(".ttf");
            hints.LicenseFileNames.Clear(); hints.LicenseFileNames.Add("readme.txt");

            using var ms = new MemoryStream(zip);
            var result = await inspector.InspectAsync(ms, hints, CancellationToken.None);

            Assert.Single(result.Fonts);
            Assert.Equal("MyFont-Regular.ttf", result.Fonts[0].EntryName);
            Assert.DoesNotContain(result.Fonts, f => f.EntryName.EndsWith(".otf"));
            Assert.NotNull(result.LicenseText);
            Assert.Contains("personal use", result.LicenseText);
            Assert.Single(result.LicenseFileNames);
        }

        [Fact]
        public async Task Valid_zip_with_no_fonts_returns_empty_result()
        {
            byte[] zip = ZipBuilder.Build(new[]
            {
                ("notes.txt", ZipBuilder.Text("hello")),
                ("image.jpg", new byte[] { 1, 2 }),
            });

            using var ms = new MemoryStream(zip);
            var result = await new FontArchiveInspector().InspectAsync(ms, null, CancellationToken.None);

            Assert.Empty(result.Fonts);
        }

        [Fact]
        public async Task Non_zip_bytes_throw_InvalidDataException_with_clear_message()
        {
            byte[] html = System.Text.Encoding.UTF8.GetBytes("<html><head><title>Error</title></head><body>403</body></html>");

            using var ms = new MemoryStream(html);
            var ex = await Assert.ThrowsAsync<InvalidDataException>(
                () => new FontArchiveInspector().InspectAsync(ms, null, CancellationToken.None));

            Assert.Contains("not a ZIP file", ex.Message);
            Assert.DoesNotContain("Central Directory", ex.Message);
        }

        [Fact]
        public async Task Zip_magic_check_passes_for_valid_zip()
        {
            byte[] zip = ZipBuilder.Build(new[]
            {
                ("Font-Regular.ttf", TtfTestBuilder.BuildValidTtf("Font")),
            });

            using var ms = new MemoryStream(zip);
            var result = await new FontArchiveInspector().InspectAsync(ms, null, CancellationToken.None);

            Assert.Single(result.Fonts);
        }
    }
}

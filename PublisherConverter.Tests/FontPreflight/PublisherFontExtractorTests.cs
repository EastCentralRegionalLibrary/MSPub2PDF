using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using OpenMcdf;
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
    // -name records that share the font records' byte shapes, plus a FONT name
    // table padded with the default font of every enabled editing language —
    // the exact things that broke earlier parsers. The fonts that actually
    // render are exactly {Calibri, Century Gothic, Lemon Cookie Bold, New York}.
    // Cambria appears only in an unapplied template heading style, and Symbol
    // only in Publisher's built-in bullet-scheme gallery — neither is rendered,
    // and neither may be reported.
    public sealed class PublisherFontExtractorTests
    {
        private const string Sample = "TestData/TestFile.pub";

        // The verified rendered set — the regression guard for the referenced-font
        // contract. An over-reporting parser fails the exact-set comparison.
        private static readonly string[] RenderedFonts =
        {
            "Calibri", "Century Gothic", "Lemon Cookie Bold", "New York",
        };

        [Fact]
        public void Extracts_exactly_the_referenced_fonts()
        {
            var fonts = new PublisherFontExtractor().ExtractFontNames(Sample);

            Assert.Equal(RenderedFonts, fonts.OrderBy(f => f, StringComparer.Ordinal));
        }

        [Fact]
        public void Does_not_report_table_resident_fonts_that_never_render()
        {
            var fonts = new PublisherFontExtractor().ExtractFontNames(Sample);

            // Editing-language defaults injected into the FONT lookup table.
            Assert.DoesNotContain("Mangal", fonts);
            Assert.DoesNotContain("SimSun", fonts);
            Assert.DoesNotContain("Times New Roman", fonts);
            // Unapplied template heading style.
            Assert.DoesNotContain("Cambria", fonts);
            // Bullet-scheme gallery boilerplate (03 C0 records).
            Assert.DoesNotContain("Symbol", fonts);
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

        // ------------------------------------------------------------------
        // Structure-level tests against the fixture's real Quill stream.
        // ------------------------------------------------------------------

        private static byte[] ReadQuillStream()
        {
            using var fs = new FileStream(Sample, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var cf = new CompoundFile(fs);
            return cf.RootStorage.GetStorage("Quill").GetStorage("QuillSub").GetStream("CONTENTS").GetData();
        }

        [Fact]
        public void ParseChunkDirectory_finds_font_and_fdpc_chunks()
        {
            var chunks = PublisherFontExtractor.ParseChunkDirectory(ReadQuillStream());

            Assert.Contains(chunks, c => c.Type == "FONT");
            Assert.True(chunks.Count(c => c.Type == "FDPC") >= 1);
        }

        [Fact]
        public void ParseChunkDirectory_returns_empty_for_non_chnk_buffer()
        {
            var chunks = PublisherFontExtractor.ParseChunkDirectory(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            Assert.Empty(chunks);
        }

        [Fact]
        public void ParseFontTable_reads_all_entries_positionally()
        {
            byte[] quill = ReadQuillStream();
            var fontChunk = PublisherFontExtractor.ParseChunkDirectory(quill).First(c => c.Type == "FONT");

            var table = PublisherFontExtractor.ParseFontTable(quill, fontChunk.Offset, fontChunk.Length);

            Assert.Equal(35, table.Count);
            Assert.Equal("Calibri", table[0]);
            Assert.Equal("Century Gothic", table[32]);
            Assert.Equal("Lemon Cookie Bold", table[33]);
            Assert.Equal("New York", table[34]);
        }

        [Fact]
        public void HarvestRunFontIndices_finds_exactly_the_run_bound_indices()
        {
            byte[] quill = ReadQuillStream();
            var fdpc = PublisherFontExtractor.ParseChunkDirectory(quill)
                .Where(c => c.Type == "FDPC")
                .Select(c => (c.Offset, c.Length));

            var indices = PublisherFontExtractor.HarvestRunFontIndices(quill, fdpc);

            Assert.Equal(new[] { 32, 33, 34 }, indices.OrderBy(i => i));
        }

        // ------------------------------------------------------------------
        // Synthetic malformed-input tests: contribute nothing, never throw.
        // ------------------------------------------------------------------

        [Fact]
        public void ParseFontTable_with_count_exceeding_buffer_returns_empty()
        {
            var chunk = new byte[0x20];
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(0, 4), 0x20);   // chunkLen
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4, 4), 1000);   // count: offset table can't fit

            var table = PublisherFontExtractor.ParseFontTable(chunk, 0, chunk.Length);

            Assert.Empty(table);
        }

        [Fact]
        public void MapIndicesToNames_skips_out_of_range_indices()
        {
            var table = new[] { "Calibri", "Cambria" };

            var names = PublisherFontExtractor.MapIndicesToNames(table, new[] { 0, 5, 200, -1 });

            Assert.Equal(new[] { "Calibri" }, names);
        }

        [Fact]
        public void HarvestRunFontIndices_accepts_out_of_table_index_without_throwing()
        {
            // The harvester has no table knowledge; range filtering happens in
            // MapIndicesToNames. A synthetic page binding index 200 must simply
            // surface 200 without throwing.
            byte[] page = BuildFdpcPage(fontIndex: 200);

            var indices = PublisherFontExtractor.HarvestRunFontIndices(page, new[] { (0, page.Length) });

            Assert.Equal(new[] { 200 }, indices.ToArray());
        }

        [Fact]
        public void HarvestRunFontIndices_ignores_truncated_property_block()
        {
            byte[] page = BuildFdpcPage(fontIndex: 33);
            // Corrupt the grpprl: totalLen now runs past the end of the page.
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(0x40, 4), 0x10000);

            var indices = PublisherFontExtractor.HarvestRunFontIndices(page, new[] { (0, page.Length) });

            Assert.Empty(indices);
        }

        [Fact]
        public void HarvestRunFontIndices_skips_unset_font_index()
        {
            byte[] page = BuildFdpcPage(fontIndex: 0xFFFF);

            var indices = PublisherFontExtractor.HarvestRunFontIndices(page, new[] { (0, page.Length) });

            Assert.Empty(indices);
        }

        // One 0x200-byte FDPC page with a single run whose property block carries
        // sprm 0x24 binding scriptId 0 → fontIndex.
        private static byte[] BuildFdpcPage(int fontIndex)
        {
            var page = new byte[0x200];
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(0, 2), 1);       // cfod
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(2, 2), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8, 4), 0x80);    // fcLim[0]
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(12, 2), 0x40);   // propOffset[0]

            // grpprl at 0x40: [u32 totalLen][sprm 0x24 type 0x8A][u32 size][10-byte sub-record]
            int block = 0x40;
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(block, 4), 4 + 2 + 4 + 10);
            page[block + 4] = 0x24;                                               // sprmId
            page[block + 5] = 0x8A;                                               // type
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(block + 6, 4), 4 + 10);
            int sub = block + 10;
            page[sub] = 0x00;                                                     // scriptId
            page[sub + 1] = 0x88;                                                 // marker
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(sub + 2, 2), 0x0008);
            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(sub + 4, 4), 0x18000000);
            BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(sub + 8, 2), (ushort)fontIndex);
            return page;
        }
    }
}

using System.Text;
using PublisherConverter.Core.FontSources;
using Xunit;

namespace PublisherConverter.Tests.FontSources
{
    public sealed class FontIntegrityValidatorTests
    {
        [Fact]
        public void Accepts_a_real_ttf()
        {
            byte[] ttf = TtfTestBuilder.BuildValidTtf("Acme Sans");
            Assert.True(FontIntegrityValidator.Validate(ttf).IsValidTtf);
        }

        [Fact]
        public void Rejects_empty_and_tiny_payloads()
        {
            Assert.False(FontIntegrityValidator.Validate(null).IsValidTtf);
            Assert.False(FontIntegrityValidator.Validate(new byte[] { 0, 0, 0 }).IsValidTtf);
        }

        [Fact]
        public void Rejects_opentype_cff_mislabeled_as_ttf()
        {
            // 'OTTO' sfnt version = OpenType/CFF (.otf).
            var data = new byte[] { 0x4F, 0x54, 0x54, 0x4F, 0, 1, 0, 0, 0, 0, 0, 0 };
            var result = FontIntegrityValidator.Validate(data);
            Assert.False(result.IsValidTtf);
            Assert.Contains("OpenType", result.Reason);
        }

        [Fact]
        public void Rejects_woff_and_woff2()
        {
            var woff = Encoding.ASCII.GetBytes("wOFF" + new string('\0', 16));
            var woff2 = Encoding.ASCII.GetBytes("wOF2" + new string('\0', 16));
            Assert.False(FontIntegrityValidator.Validate(woff).IsValidTtf);
            Assert.False(FontIntegrityValidator.Validate(woff2).IsValidTtf);
        }

        [Fact]
        public void Rejects_ttc_collection()
        {
            var ttc = new byte[] { 0x74, 0x74, 0x63, 0x66, 0, 1, 0, 0, 0, 0, 0, 1 };
            var result = FontIntegrityValidator.Validate(ttc);
            Assert.False(result.IsValidTtf);
            Assert.Contains("Collection", result.Reason);
        }

        [Fact]
        public void Rejects_truetype_without_glyf_table()
        {
            // Valid sfnt header with 'head' + 'name' but no 'glyf' outlines.
            byte[] data = BuildHeadNameSfnt();
            var result = FontIntegrityValidator.Validate(data);
            Assert.False(result.IsValidTtf);
            Assert.Contains("glyf", result.Reason);
        }

        private static byte[] BuildHeadNameSfnt()
        {
            using var ms = new System.IO.MemoryStream();
            void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
            void U32(uint v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
            const int numTables = 2;
            int dataStart = 12 + numTables * 16;
            int headOff = dataStart, headLen = 54;
            int nameOff = headOff + headLen, nameLen = 8;
            U32(0x00010000); U16(numTables); U16(0); U16(0); U16(0);
            U32(0x68656164); U32(0); U32((uint)headOff); U32((uint)headLen); // 'head'
            U32(0x6E616D65); U32(0); U32((uint)nameOff); U32((uint)nameLen);  // 'name'
            ms.Write(new byte[headLen], 0, headLen);
            ms.Write(new byte[nameLen], 0, nameLen);
            return ms.ToArray();
        }
    }
}

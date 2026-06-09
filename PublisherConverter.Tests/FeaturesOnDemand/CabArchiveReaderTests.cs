using System;
using System.Linq;
using System.Text;
using PublisherConverter.Core.FeaturesOnDemand;
using Xunit;

namespace PublisherConverter.Tests.FeaturesOnDemand
{
    public sealed class CabArchiveReaderTests
    {
        private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

        [Fact]
        public void Reads_uncompressed_cab_entries()
        {
            byte[] a = Bytes("hello world");
            byte[] b = Bytes("second file contents");
            byte[] cab = CabBuilder.Build(new[] { ("a.txt", a), ("b.ttf", b) }, CabCompression.None);

            var reader = new CabArchiveReader(cab);

            Assert.Equal(new[] { "a.txt", "b.ttf" }, reader.Entries.Select(e => e.Name));
            Assert.Equal(CabCompression.None, reader.CompressionOf(0));
            Assert.Equal(a, reader.ReadEntry(reader.Entries[0]));
            Assert.Equal(b, reader.ReadEntry(reader.Entries[1]));
        }

        [Fact]
        public void Reads_mszip_cab_entries()
        {
            byte[] a = Bytes(new string('A', 5000) + "-marker-" + new string('B', 3000));
            byte[] cab = CabBuilder.Build(new[] { ("a.ttf", a) }, CabCompression.MsZip);

            var reader = new CabArchiveReader(cab);

            Assert.Equal(CabCompression.MsZip, reader.CompressionOf(0));
            Assert.Equal(a, reader.ReadEntry(reader.Entries[0]));
        }

        [Fact]
        public void Reads_multi_block_mszip_spanning_32k_boundary()
        {
            // > 32768 bytes forces more than one CFDATA block.
            var sb = new StringBuilder();
            for (int i = 0; i < 70000; i++) sb.Append((char)('a' + (i % 26)));
            byte[] a = Bytes(sb.ToString());

            byte[] cab = CabBuilder.Build(new[] { ("big.ttf", a) }, CabCompression.MsZip);
            var reader = new CabArchiveReader(cab);

            Assert.Equal(a, reader.ReadEntry(reader.Entries[0]));
        }

        [Fact]
        public void Rejects_non_cab_input()
        {
            Assert.Throws<System.IO.InvalidDataException>(() => new CabArchiveReader(new byte[] { 0, 1, 2, 3, 4, 5 }));
        }
    }
}

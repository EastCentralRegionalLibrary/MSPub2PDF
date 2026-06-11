using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PublisherConverter.Core.FeaturesOnDemand;

namespace PublisherConverter.Tests.FeaturesOnDemand
{
    /// <summary>
    /// Builds a minimal but valid MS-CAB in memory (single folder), so the
    /// managed reader can be exercised without shipping binary fixtures.
    /// Supports the two methods the managed reader decompresses: None (stored)
    /// and MSZIP. MSZIP blocks are compressed independently — still valid MSZIP,
    /// and enough to drive the reader's multi-block plumbing.
    /// </summary>
    internal static class CabBuilder
    {
        private const int MaxBlock = 32768;

        public static byte[] Build(IReadOnlyList<(string Name, byte[] Data)> files, CabCompression compression)
        {
            // Folder uncompressed stream = concatenation of all file payloads.
            using var folderStream = new MemoryStream();
            var fileRecords = new List<(string Name, uint Offset, uint Size)>();
            foreach (var (name, data) in files)
            {
                fileRecords.Add((name, (uint)folderStream.Length, (uint)data.Length));
                folderStream.Write(data, 0, data.Length);
            }
            byte[] folder = folderStream.ToArray();

            // Build CFDATA blocks.
            using var cfdata = new MemoryStream();
            ushort blockCount = 0;
            int pos = 0;
            while (pos < folder.Length || (folder.Length == 0 && blockCount == 0))
            {
                int len = Math.Min(MaxBlock, folder.Length - pos);
                if (len < 0) len = 0;
                var chunk = new byte[len];
                Array.Copy(folder, pos, chunk, 0, len);
                pos += len;

                byte[] blockData;
                if (compression == CabCompression.MsZip)
                {
                    using var deflated = new MemoryStream();
                    using (var ds = new DeflateStream(deflated, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        ds.Write(chunk, 0, chunk.Length);
                    }
                    byte[] def = deflated.ToArray();
                    blockData = new byte[2 + def.Length];
                    blockData[0] = 0x43; // 'C'
                    blockData[1] = 0x4B; // 'K'
                    Array.Copy(def, 0, blockData, 2, def.Length);
                }
                else
                {
                    blockData = chunk;
                }

                WriteU32(cfdata, 0);                    // csum (unchecked by the reader)
                WriteU16(cfdata, (ushort)blockData.Length); // cbData
                WriteU16(cfdata, (ushort)len);          // cbUncomp
                cfdata.Write(blockData, 0, blockData.Length);
                blockCount++;

                if (folder.Length == 0) break;
            }
            byte[] cfdataBytes = cfdata.ToArray();

            // CFFILE table.
            using var cffile = new MemoryStream();
            foreach (var (name, offset, size) in fileRecords)
            {
                WriteU32(cffile, size);    // cbFile
                WriteU32(cffile, offset);  // uoffFolderStart
                WriteU16(cffile, 0);       // iFolder = 0
                WriteU16(cffile, 0);       // date
                WriteU16(cffile, 0);       // time
                WriteU16(cffile, 0);       // attribs (ASCII name)
                byte[] nameBytes = Encoding.ASCII.GetBytes(name);
                cffile.Write(nameBytes, 0, nameBytes.Length);
                cffile.WriteByte(0);       // null terminator
            }
            byte[] cffileBytes = cffile.ToArray();

            const int headerSize = 36;
            const int folderSize = 8;
            int coffFiles = headerSize + folderSize;
            int coffCabStart = coffFiles + cffileBytes.Length;
            int total = coffCabStart + cfdataBytes.Length;

            using var output = new MemoryStream();
            // CFHEADER
            output.Write(Encoding.ASCII.GetBytes("MSCF"), 0, 4);
            WriteU32(output, 0);                 // reserved1
            WriteU32(output, (uint)total);       // cbCabinet
            WriteU32(output, 0);                 // reserved2
            WriteU32(output, (uint)coffFiles);   // coffFiles
            WriteU32(output, 0);                 // reserved3
            output.WriteByte(3);                 // versionMinor
            output.WriteByte(1);                 // versionMajor
            WriteU16(output, 1);                 // cFolders
            WriteU16(output, (ushort)fileRecords.Count); // cFiles
            WriteU16(output, 0);                 // flags
            WriteU16(output, 0);                 // setID
            WriteU16(output, 0);                 // iCabinet
            // CFFOLDER
            WriteU32(output, (uint)coffCabStart);    // coffCabStart
            WriteU16(output, blockCount);            // cCFData
            WriteU16(output, (ushort)compression);   // typeCompress
            // CFFILE table then CFDATA blocks
            output.Write(cffileBytes, 0, cffileBytes.Length);
            output.Write(cfdataBytes, 0, cfdataBytes.Length);

            return output.ToArray();
        }

        private static void WriteU16(Stream s, ushort v)
        {
            s.WriteByte((byte)(v & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
        }

        private static void WriteU32(Stream s, uint v)
        {
            s.WriteByte((byte)(v & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
            s.WriteByte((byte)((v >> 16) & 0xFF));
            s.WriteByte((byte)((v >> 24) & 0xFF));
        }
    }

    /// <summary>
    /// Builds minimal TrueType / TrueType-Collection blobs carrying a real
    /// <c>name</c> table, so <see cref="FontFileInspector"/> can be tested
    /// without binary fixtures.
    /// </summary>
    internal static class FontBuilder
    {
        public static byte[] BuildTtf(string family) => BuildSfnt(family, baseOffset: 0);

        public static byte[] BuildTtc(params string[] families)
        {
            // sfnt length is independent of base offset, so size first, then lay
            // out, then build each sub-font with its absolute base offset (table
            // directory offsets are absolute from the start of the file).
            int headerSize = 12 + 4 * families.Length;
            var lengths = families.Select(f => BuildSfnt(f, 0).Length).ToArray();
            var baseOffsets = new int[families.Length];
            int running = headerSize;
            for (int i = 0; i < families.Length; i++)
            {
                baseOffsets[i] = running;
                running += lengths[i];
            }

            using var ms = new MemoryStream();
            WriteU32(ms, 0x74746366); // 'ttcf'
            WriteU16(ms, 1);          // majorVersion
            WriteU16(ms, 0);          // minorVersion
            WriteU32(ms, (uint)families.Length);
            foreach (var off in baseOffsets) WriteU32(ms, (uint)off);
            for (int i = 0; i < families.Length; i++)
            {
                byte[] sub = BuildSfnt(families[i], baseOffsets[i]);
                ms.Write(sub, 0, sub.Length);
            }
            return ms.ToArray();
        }

        private static byte[] BuildSfnt(string family, int baseOffset)
        {
            byte[] nameTable = BuildNameTable(family);

            using var ms = new MemoryStream();
            // Offset table
            WriteU32(ms, 0x00010000); // sfntVersion (TrueType)
            WriteU16(ms, 1);          // numTables
            WriteU16(ms, 16);         // searchRange
            WriteU16(ms, 0);          // entrySelector
            WriteU16(ms, 0);          // rangeShift
            // Single table record for 'name' — offset is absolute from file start.
            int tableOffset = baseOffset + 12 + 16;
            WriteU32(ms, 0x6E616D65);          // 'name'
            WriteU32(ms, 0);                   // checksum
            WriteU32(ms, (uint)tableOffset);   // offset
            WriteU32(ms, (uint)nameTable.Length); // length
            ms.Write(nameTable, 0, nameTable.Length);
            return ms.ToArray();
        }

        private static byte[] BuildNameTable(string family)
        {
            byte[] value = Encoding.BigEndianUnicode.GetBytes(family); // UTF-16BE
            const int recordCount = 1;
            int headerLen = 6 + 12 * recordCount;

            using var ms = new MemoryStream();
            WriteU16(ms, 0);               // format
            WriteU16(ms, recordCount);     // count
            WriteU16(ms, (ushort)headerLen); // stringOffset
            // Name record: Windows (3), Unicode BMP (1), US English (0x409), Family (1)
            WriteU16(ms, 3);
            WriteU16(ms, 1);
            WriteU16(ms, 0x0409);
            WriteU16(ms, 1);
            WriteU16(ms, (ushort)value.Length);
            WriteU16(ms, 0);               // offset into storage
            ms.Write(value, 0, value.Length);
            return ms.ToArray();
        }

        private static void WriteU16(Stream s, int v)
        {
            s.WriteByte((byte)((v >> 8) & 0xFF));
            s.WriteByte((byte)(v & 0xFF));
        }

        private static void WriteU32(Stream s, uint v)
        {
            s.WriteByte((byte)((v >> 24) & 0xFF));
            s.WriteByte((byte)((v >> 16) & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
            s.WriteByte((byte)(v & 0xFF));
        }
    }

    /// <summary>Builds PKCS#7 SignedData blobs with a throwaway self-signed cert.</summary>
    internal static class SignedCmsBuilder
    {
        public static (byte[] pkcs7, X509Certificate2 cert) Build(string subject, byte[] content)
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest($"CN={subject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            var signedCms = new SignedCms(new ContentInfo(content), detached: false);
            var signer = new CmsSigner(cert) { IncludeOption = X509IncludeOption.EndCertOnly };
            signedCms.ComputeSignature(signer);
            return (signedCms.Encode(), cert);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PublisherConverter.Core.FeaturesOnDemand
{
    /// <summary>MS-CAB folder compression methods (low nibble of typeCompress).</summary>
    public enum CabCompression
    {
        None = 0,
        MsZip = 1,
        Quantum = 2,
        Lzx = 3,
    }

    /// <summary>One file entry in a cabinet.</summary>
    public sealed class CabEntry
    {
        public required string Name { get; init; }
        public long Size { get; init; }
        public int FolderIndex { get; init; }
        public uint FolderOffset { get; init; }
    }

    /// <summary>
    /// A minimal, dependency-free Microsoft Cabinet (MS-CAB) reader. It fully
    /// parses the CFHEADER / CFFOLDER / CFFILE structures so contents can be
    /// enumerated on any platform, and decompresses the <c>None</c> (stored) and
    /// <c>MSZIP</c> folder formats. <c>MSZIP</c> is DEFLATE with a per-block
    /// preset dictionary (the previous block's output); since .NET's
    /// <see cref="DeflateStream"/> has no preset-dictionary API, each block is
    /// inflated by prepending the carried-over history as a stored DEFLATE block
    /// and discarding it from the output — the standard MSZIP technique.
    ///
    /// LZX and Quantum folders are recognised but not decompressed here; on
    /// Windows the native <c>expand.exe</c> path handles every method. Enumeration
    /// works regardless of compression because file metadata lives in CFFILE,
    /// which is never compressed.
    /// </summary>
    public sealed class CabArchiveReader
    {
        private const ushort FlagReservePresent = 0x0004;
        private const ushort FlagPrevCabinet = 0x0001;
        private const ushort FlagNextCabinet = 0x0002;
        private const ushort FileNameIsUtf8 = 0x80;
        private const int MaxWindow = 32768;

        private readonly byte[] _data;
        private readonly List<CabFolderInfo> _folders = new();
        private readonly List<CabEntry> _entries = new();
        private readonly byte[]?[] _decompressedFolders;
        private readonly byte _cbCFData;

        public IReadOnlyList<CabEntry> Entries => _entries;

        public CabArchiveReader(byte[] cabBytes)
        {
            _data = cabBytes ?? throw new ArgumentNullException(nameof(cabBytes));
            ParseHeaderAndDirectory(out _cbCFData);
            _decompressedFolders = new byte[_folders.Count][];
        }

        public static CabArchiveReader FromFile(string path) => new CabArchiveReader(File.ReadAllBytes(path));

        public CabCompression CompressionOf(int folderIndex)
            => (CabCompression)(_folders[folderIndex].TypeCompress & 0x000F);

        /// <summary>Returns the uncompressed bytes of one entry.</summary>
        public byte[] ReadEntry(CabEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (entry.FolderIndex < 0 || entry.FolderIndex >= _folders.Count)
                throw new InvalidDataException($"Entry '{entry.Name}' references folder {entry.FolderIndex} which does not exist.");

            byte[] folder = GetDecompressedFolder(entry.FolderIndex);
            long start = entry.FolderOffset;
            long size = entry.Size;
            if (start < 0 || start + size > folder.Length)
                throw new InvalidDataException($"Entry '{entry.Name}' extends past the decompressed folder data.");

            var output = new byte[size];
            Array.Copy(folder, start, output, 0, size);
            return output;
        }

        public void ExtractEntryTo(CabEntry entry, Stream destination)
        {
            byte[] bytes = ReadEntry(entry);
            destination.Write(bytes, 0, bytes.Length);
        }

        // ------------------------------------------------------------------

        private void ParseHeaderAndDirectory(out byte cbCFData)
        {
            if (_data.Length < 36 || _data[0] != 'M' || _data[1] != 'S' || _data[2] != 'C' || _data[3] != 'F')
                throw new InvalidDataException("Not a Microsoft Cabinet file (missing MSCF signature).");

            uint coffFiles = ReadU32(16);
            ushort cFolders = ReadU16(26);
            ushort cFiles = ReadU16(28);
            ushort flags = ReadU16(30);

            int pos = 36;
            byte cbCFFolder = 0;
            cbCFData = 0;
            if ((flags & FlagReservePresent) != 0)
            {
                if (pos + 4 > _data.Length) throw new InvalidDataException("Truncated CFHEADER reserve fields.");
                ushort cbCFHeader = ReadU16(pos);
                cbCFFolder = _data[pos + 2];
                cbCFData = _data[pos + 3];
                pos += 4 + cbCFHeader;
            }
            if ((flags & FlagPrevCabinet) != 0)
            {
                pos = SkipCString(pos); // szCabinetPrev
                pos = SkipCString(pos); // szDiskPrev
            }
            if ((flags & FlagNextCabinet) != 0)
            {
                pos = SkipCString(pos); // szCabinetNext
                pos = SkipCString(pos); // szDiskNext
            }

            // CFFOLDER entries follow the (optional) header reserve.
            for (int i = 0; i < cFolders; i++)
            {
                if (pos + 8 > _data.Length) throw new InvalidDataException("Truncated CFFOLDER table.");
                var folder = new CabFolderInfo
                {
                    CoffCabStart = ReadU32(pos),
                    CCFData = ReadU16(pos + 4),
                    TypeCompress = ReadU16(pos + 6),
                };
                _folders.Add(folder);
                pos += 8 + cbCFFolder;
            }

            // CFFILE entries begin at coffFiles. Validate the offset before the
            // uint→int cast so a crafted out-of-range value is rejected cleanly
            // rather than truncating to a negative index.
            if (coffFiles > (uint)_data.Length) throw new InvalidDataException("coffFiles offset is out of range.");
            int filePos = (int)coffFiles;
            for (int i = 0; i < cFiles; i++)
            {
                if (filePos < 0 || filePos + 16 > _data.Length) throw new InvalidDataException("Truncated CFFILE table.");
                uint cbFile = ReadU32(filePos);
                uint uoffFolderStart = ReadU32(filePos + 4);
                ushort iFolder = ReadU16(filePos + 8);
                ushort attribs = ReadU16(filePos + 14);
                int nameStart = filePos + 16;
                (string name, int afterName) = ReadCString(nameStart, (attribs & FileNameIsUtf8) != 0);

                _entries.Add(new CabEntry
                {
                    Name = name,
                    Size = cbFile,
                    FolderIndex = iFolder,
                    FolderOffset = uoffFolderStart,
                });
                filePos = afterName;
            }
        }

        private byte[] GetDecompressedFolder(int folderIndex)
        {
            if (_decompressedFolders[folderIndex] is { } cached) return cached;

            var folder = _folders[folderIndex];
            CabCompression compression = (CabCompression)(folder.TypeCompress & 0x000F);

            byte[] result = compression switch
            {
                CabCompression.None => DecompressFolderNone(folder),
                CabCompression.MsZip => DecompressFolderMsZip(folder),
                _ => throw new NotSupportedException(
                    $"CAB folder uses {compression} compression, which the managed reader does not support. " +
                    "Use the native expand.exe extractor on Windows."),
            };

            _decompressedFolders[folderIndex] = result;
            return result;
        }

        private byte[] DecompressFolderNone(CabFolderInfo folder)
        {
            using var output = new MemoryStream();
            int pos = (int)folder.CoffCabStart;
            for (int b = 0; b < folder.CCFData; b++)
            {
                (int dataPos, int cbData, int cbUncomp, int next) = ReadCfData(pos);
                output.Write(_data, dataPos, cbData == 0 ? cbUncomp : cbData);
                pos = next;
            }
            return output.ToArray();
        }

        private byte[] DecompressFolderMsZip(CabFolderInfo folder)
        {
            using var output = new MemoryStream();
            byte[] history = Array.Empty<byte>();
            int pos = (int)folder.CoffCabStart;

            for (int b = 0; b < folder.CCFData; b++)
            {
                (int dataPos, int cbData, int cbUncomp, int next) = ReadCfData(pos);
                pos = next;

                if (cbData < 2 || _data[dataPos] != 0x43 || _data[dataPos + 1] != 0x4B) // 'CK'
                    throw new InvalidDataException("MSZIP data block missing 'CK' signature.");

                var deflate = new byte[cbData - 2];
                Array.Copy(_data, dataPos + 2, deflate, 0, deflate.Length);

                byte[] block = InflateBlock(history, deflate, cbUncomp);
                output.Write(block, 0, block.Length);

                history = TailWindow(output);
            }

            return output.ToArray();
        }

        // Inflate one DEFLATE block, seeding the sliding window with the previous
        // block's output by prepending it as a stored (uncompressed) block.
        private static byte[] InflateBlock(byte[] history, byte[] deflate, int expectedSize)
        {
            if (history.Length == 0)
            {
                using var src = new MemoryStream(deflate, writable: false);
                using var inflate = new DeflateStream(src, CompressionMode.Decompress);
                return ReadExact(inflate, expectedSize);
            }

            using var combined = new MemoryStream(history.Length + 5 + deflate.Length);
            // Stored DEFLATE block: BFINAL=0, BTYPE=00 → 0x00, then LEN/NLEN (LE), then literal bytes.
            combined.WriteByte(0x00);
            int len = history.Length;
            combined.WriteByte((byte)(len & 0xFF));
            combined.WriteByte((byte)((len >> 8) & 0xFF));
            int nlen = (~len) & 0xFFFF;
            combined.WriteByte((byte)(nlen & 0xFF));
            combined.WriteByte((byte)((nlen >> 8) & 0xFF));
            combined.Write(history, 0, history.Length);
            combined.Write(deflate, 0, deflate.Length);
            combined.Position = 0;

            using var inflate2 = new DeflateStream(combined, CompressionMode.Decompress);
            byte[] all = ReadExact(inflate2, history.Length + expectedSize);
            var result = new byte[expectedSize];
            Array.Copy(all, history.Length, result, 0, expectedSize);
            return result;
        }

        private static byte[] TailWindow(MemoryStream output)
        {
            int take = (int)Math.Min(MaxWindow, output.Length);
            var window = new byte[take];
            output.Position = output.Length - take;
            int read = 0;
            while (read < take)
            {
                int n = output.Read(window, read, take - read);
                if (n <= 0) break;
                read += n;
            }
            output.Position = output.Length;
            return window;
        }

        private (int dataPos, int cbData, int cbUncomp, int next) ReadCfData(int pos)
        {
            // pos originates from a uint folder offset cast to int; a crafted
            // out-of-range value turns negative, so guard both ends.
            if (pos < 0 || pos + 8 > _data.Length) throw new InvalidDataException("Truncated CFDATA block header.");
            int cbData = ReadU16(pos + 4);
            int cbUncomp = ReadU16(pos + 6);
            int dataPos = pos + 8 + _cbCFData;
            if (dataPos + cbData > _data.Length) throw new InvalidDataException("CFDATA block extends past end of file.");
            return (dataPos, cbData, cbUncomp, dataPos + cbData);
        }

        private static byte[] ReadExact(Stream stream, int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0) throw new InvalidDataException("Compressed block produced fewer bytes than expected.");
                read += n;
            }
            return buffer;
        }

        private int SkipCString(int pos)
        {
            while (pos < _data.Length && _data[pos] != 0) pos++;
            return pos + 1;
        }

        private (string value, int next) ReadCString(int pos, bool utf8)
        {
            int start = pos;
            while (pos < _data.Length && _data[pos] != 0) pos++;
            int len = pos - start;
            string value = (utf8 ? Encoding.UTF8 : Encoding.ASCII).GetString(_data, start, len);
            return (value, pos + 1);
        }

        private uint ReadU32(int o)
            => (uint)(_data[o] | (_data[o + 1] << 8) | (_data[o + 2] << 16) | (_data[o + 3] << 24));

        private ushort ReadU16(int o)
            => (ushort)(_data[o] | (_data[o + 1] << 8));

        private sealed class CabFolderInfo
        {
            public uint CoffCabStart { get; init; }
            public ushort CCFData { get; init; }
            public ushort TypeCompress { get; init; }
        }
    }
}

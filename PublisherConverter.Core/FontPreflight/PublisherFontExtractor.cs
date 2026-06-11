using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using OpenMcdf;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Extracts the font family names REFERENCED by a Microsoft Publisher (.pub) publication
    /// WITHOUT opening it in Publisher.
    ///
    /// A .pub is an OLE2 / Compound File Binary document. The text engine's stream
    /// (Quill/QuillSub/CONTENTS) is a CHNK chunk file:
    ///
    ///   * The directory (entries of 0x18 bytes from 0x20) locates typed chunks. The FONT chunk
    ///     is the engine's font NAME TABLE — a lookup table, not a usage list. Office's
    ///     enabled-editing-languages feature injects the default font for every enabled script
    ///     into it, so dumping the table over-reports by an order of magnitude (35 names vs the
    ///     4 that actually render, on the test fixture).
    ///   * Actual usage lives in character-run formatting: FDPC pages hold property blocks
    ///     (grpprl) whose sprm 0x24 (type 0x8A) payload binds script ids to FONT-table indices.
    ///     Harvesting those indices and resolving them through the table yields the fonts the
    ///     runs really use.
    ///
    ///   Contents -- per-shape / WordArt font references. A font record is the byte tag
    ///       03 10 00 00 immediately followed by marker 04 C0, then [u32 totalLen]
    ///       [UTF-16LE name]. Page/section-name records use marker 0F C0 / 0E C0 and lack the
    ///       03 10 00 00 tag, so requiring the tag excludes them. These supply the default-styled
    ///       text's font (e.g. Calibri) when no run-level binding exists.
    ///
    /// The reported set is the union: FDPC-referenced table names ∪ Contents 04 C0 names.
    /// If the CHNK directory or FONT chunk fails to parse, only the Contents-stream names are
    /// reported — the parser never falls back to a heuristic table scan.
    ///
    /// Known limitation: a WordArt font stored under marker 06 C0 (rather than 04 C0) is not
    /// captured, because that marker is shared with page-label records and cannot be
    /// disambiguated by local structure. The out-of-process worker's exit code remains the
    /// backstop for any missing font that slips past pre-flight.
    /// </summary>
    public interface IPublisherFontExtractor
    {
        IReadOnlySet<string> ExtractFontNames(string pubPath);
    }

    public sealed class PublisherFontExtractor : IPublisherFontExtractor
    {
        private static readonly Regex FontNameRegex =
            new Regex(@"^[A-Za-z][A-Za-z0-9 \-]{1,40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // 03 10 00 00 immediately precedes the 04 C0 marker on genuine Contents font records.
        private static readonly byte[] ContentsFontTag = { 0x03, 0x10, 0x00, 0x00 };

        private static readonly string[] QuillStreamPath = { "Quill", "QuillSub", "CONTENTS" };
        private static readonly string[] ContentsStreamPath = { "Contents" };

        // CHNK directory constants (§ layout: [u16 entrySize][char4 name][u16 nameIndex]
        // [u32 1][char4 type][u32 offset][u32 length]).
        private const int DirectoryStart = 0x20;
        private const int DirectoryEntrySize = 0x18;

        // FONT chunk: the entry-offset table starts at 0x14 and its values are relative
        // to 0x14 (NOT 0x18 — entry 0 being exactly 0x14 bytes long on some files makes a
        // 0x18 base appear to work there; it breaks on others).
        private const int FontTableBase = 0x14;

        // Character-run font binding: sprm 0x24, operand type 0x8A (variable-length).
        private const byte FontBindingSprmId = 0x24;
        private const int FontBindingSubRecordSize = 10;

        public IReadOnlySet<string> ExtractFontNames(string pubPath)
        {
            var fonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            byte[]? quill = TryReadStream(pubPath, QuillStreamPath);
            if (quill != null)
            {
                var chunks = ParseChunkDirectory(quill);

                QuillChunk? fontChunk = null;
                var fdpcChunks = new List<(int Offset, int Length)>();
                foreach (var chunk in chunks)
                {
                    if (fontChunk == null && chunk.Type == "FONT") fontChunk = chunk;
                    else if (chunk.Type == "FDPC") fdpcChunks.Add((chunk.Offset, chunk.Length));
                }

                if (fontChunk != null)
                {
                    var table = ParseFontTable(quill, fontChunk.Value.Offset, fontChunk.Value.Length);
                    if (table.Count > 0)
                    {
                        var indices = HarvestRunFontIndices(quill, fdpcChunks);
                        foreach (var name in MapIndicesToNames(table, indices))
                            fonts.Add(name);
                    }
                }
            }

            byte[]? contents = TryReadStream(pubPath, ContentsStreamPath);
            if (contents != null)
            {
                foreach (var f in ParseContentsFontRecords(contents))
                    fonts.Add(f);
            }

            return fonts;
        }

        // ---- OLE access (OpenMcdf 2.3.x API; project pins 2.3.1) ----
        private static byte[]? TryReadStream(string path, string[] streamPath)
        {
            try
            {
                using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var cf = new CompoundFile(fileStream);
                CFStorage storage = cf.RootStorage;
                for (int i = 0; i < streamPath.Length - 1; i++)
                {
                    storage = storage.GetStorage(streamPath[i]);
                }
                return storage.GetStream(streamPath[streamPath.Length - 1]).GetData();
            }
            catch
            {
                // Missing stream / not a compound file / corrupted layout → "no data".
                // Pre-flight never throws on a bad input; static triage handles those.
                return null;
            }
        }

        /// <summary>One entry of the Quill CHNK directory.</summary>
        internal readonly record struct QuillChunk(string Name, int NameIndex, string Type, int Offset, int Length);

        // ---- CHNK directory: 0x18-byte entries from 0x20; stop on a malformed entry ----
        internal static List<QuillChunk> ParseChunkDirectory(byte[] quill)
        {
            var result = new List<QuillChunk>();
            try
            {
                if (quill == null || quill.Length < DirectoryStart + DirectoryEntrySize) return result;
                if (quill[0] != 'C' || quill[1] != 'H' || quill[2] != 'N' || quill[3] != 'K') return result;

                for (int pos = DirectoryStart; pos + DirectoryEntrySize <= quill.Length; pos += DirectoryEntrySize)
                {
                    ushort entrySize = BinaryPrimitives.ReadUInt16LittleEndian(quill.AsSpan(pos, 2));
                    if (entrySize != DirectoryEntrySize) break;

                    string? name = ReadPrintableTag(quill, pos + 0x02);
                    if (name == null) break; // non-printable name → past the directory

                    ushort nameIndex = BinaryPrimitives.ReadUInt16LittleEndian(quill.AsSpan(pos + 0x06, 2));
                    string type = ReadPrintableTag(quill, pos + 0x0C) ?? string.Empty;
                    int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(quill.AsSpan(pos + 0x10, 4));
                    int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(quill.AsSpan(pos + 0x14, 4));

                    // Unknown names/types are normal (TOKN, multiple TCD, …) — record and
                    // let the caller select; chunks with impossible extents are dropped.
                    if (offset >= 0 && length > 0 && offset <= quill.Length && offset + (long)length <= quill.Length)
                    {
                        result.Add(new QuillChunk(name.Trim(), nameIndex, type.Trim(), offset, length));
                    }
                }
            }
            catch
            {
                // Defensive: a malformed directory yields what was parsed so far.
            }
            return result;
        }

        // ---- FONT chunk: [u32 len][u32 count][u32 versionMagic — NOT validated][u32 4][u32 ?]
        //      then u32 entryOffsets[count] at 0x14, each relative to 0x14;
        //      entry = [u16 charLen][UTF-16LE name][u16 id][u8 pitch][u8 family] ----
        internal static IReadOnlyList<string> ParseFontTable(byte[] quill, int offset, int length)
        {
            var table = new List<string>();
            try
            {
                if (quill == null || offset < 0 || length < FontTableBase) return table;
                long chunkEnd = Math.Min((long)offset + length, quill.Length);
                if (offset + FontTableBase > chunkEnd) return table;

                uint count = BinaryPrimitives.ReadUInt32LittleEndian(quill.AsSpan(offset + 4, 4));
                // The offset table must fit inside the chunk — a count that exceeds the
                // buffer is a malformed file and contributes nothing.
                if (count == 0 || offset + FontTableBase + count * 4L > chunkEnd) return table;

                for (uint i = 0; i < count; i++)
                {
                    uint rel = BinaryPrimitives.ReadUInt32LittleEndian(quill.AsSpan(offset + FontTableBase + (int)(i * 4), 4));
                    long entryPos = (long)offset + FontTableBase + rel;

                    // Placeholder on a bad entry: positions must be preserved because
                    // FDPC sub-records address the table BY INDEX.
                    string name = string.Empty;
                    if (entryPos >= offset + FontTableBase && entryPos + 2 <= chunkEnd)
                    {
                        ushort charLen = BinaryPrimitives.ReadUInt16LittleEndian(quill.AsSpan((int)entryPos, 2));
                        long nameEnd = entryPos + 2 + charLen * 2L;
                        if (charLen > 0 && charLen <= 0x100 && nameEnd + 4 <= chunkEnd)
                        {
                            name = Encoding.Unicode.GetString(quill, (int)entryPos + 2, charLen * 2).TrimEnd('\0').Trim();
                        }
                    }
                    table.Add(name);
                }
            }
            catch
            {
                table.Clear(); // malformed table → contribute nothing
            }
            return table;
        }

        // ---- FDPC pages: harvest FONT-table indices from character-run property blocks ----
        internal static IReadOnlySet<int> HarvestRunFontIndices(byte[] quill, IEnumerable<(int Offset, int Length)> fdpcChunks)
        {
            var indices = new HashSet<int>();
            if (quill == null || fdpcChunks == null) return indices;

            foreach (var (offset, length) in fdpcChunks)
            {
                try
                {
                    HarvestFromPage(quill, offset, length, indices);
                }
                catch
                {
                    // Per-page isolation: a malformed page contributes nothing.
                }
            }
            return indices;
        }

        // One 0x200-byte page: [u16 cfod][u16 1][u32 ?] u32 fcLim[cfod] u16 propOffset[cfod] …blocks…
        private static void HarvestFromPage(byte[] quill, int offset, int length, HashSet<int> indices)
        {
            if (offset < 0 || length < 8) return;
            long pageEnd = Math.Min((long)offset + length, quill.Length);
            if (offset + 8 > pageEnd) return;

            ushort cfod = BinaryPrimitives.ReadUInt16LittleEndian(quill.AsSpan(offset, 2));
            long propOffsetArray = offset + 8 + cfod * 4L; // after the fcLim[cfod] u32s
            if (cfod == 0 || propOffsetArray + cfod * 2L > pageEnd) return;

            for (int i = 0; i < cfod; i++)
            {
                ushort propOffset = BinaryPrimitives.ReadUInt16LittleEndian(quill.AsSpan((int)(propOffsetArray + i * 2L), 2));
                if (propOffset == 0x0000 || propOffset == 0xFFFF) continue; // run has no properties

                long blockStart = offset + (long)propOffset; // page-relative
                if (blockStart + 4 > pageEnd) continue;

                uint totalLen = BinaryPrimitives.ReadUInt32LittleEndian(quill.AsSpan((int)blockStart, 4));
                if (totalLen < 4 || blockStart + totalLen > pageEnd) continue; // truncated grpprl

                WalkSprms(quill, (int)blockStart + 4, (int)(blockStart + totalLen), indices);
            }
        }

        // grpprl: [u8 sprmId][u8 type][payload]; unknown operand types end the block
        // (defensive — their payload layout is unknown, so resync is impossible).
        private static void WalkSprms(byte[] quill, int start, int end, HashSet<int> indices)
        {
            int pos = start;
            while (pos + 2 <= end)
            {
                byte sprmId = quill[pos];
                byte type = quill[pos + 1];
                pos += 2;

                switch (type)
                {
                    case 0x0A: // flag, no payload
                        break;

                    case 0x12: // u16
                        if (pos + 2 > end) return;
                        pos += 2;
                        break;

                    case 0x22: // u32
                        if (pos + 4 > end) return;
                        pos += 4;
                        break;

                    case 0x8A: // [u32 size (incl. itself)][size-4 bytes]
                        if (pos + 4 > end) return;
                        uint size = BinaryPrimitives.ReadUInt32LittleEndian(quill.AsSpan(pos, 4));
                        if (size < 4 || pos + size > end) return;
                        if (sprmId == FontBindingSprmId)
                        {
                            HarvestFontBindingPayload(quill, pos + 4, (int)(size - 4), indices);
                        }
                        pos += (int)size;
                        break;

                    default:
                        return; // unknown operand layout — stop walking this block
                }
            }
        }

        // sprm 0x24 payload: 10-byte sub-records [u8 scriptId][u8 0x88][u16 0x0008][u32 flags][u16 fontIndex].
        // Match on the 0x88 / 0x0008 markers only; flags vary across builds. 0xFFFF = unset.
        private static void HarvestFontBindingPayload(byte[] quill, int start, int length, HashSet<int> indices)
        {
            for (int p = start; p + FontBindingSubRecordSize <= start + length; p += FontBindingSubRecordSize)
            {
                if (quill[p + 1] != 0x88) continue;
                if (BinaryPrimitives.ReadUInt16LittleEndian(quill.AsSpan(p + 2, 2)) != 0x0008) continue;

                ushort fontIndex = BinaryPrimitives.ReadUInt16LittleEndian(quill.AsSpan(p + 8, 2));
                if (fontIndex != 0xFFFF) indices.Add(fontIndex);
            }
        }

        // Resolves harvested indices against the table; out-of-range indices and names that
        // fail validation contribute nothing.
        internal static List<string> MapIndicesToNames(IReadOnlyList<string> table, IEnumerable<int> indices)
        {
            var result = new List<string>();
            foreach (int index in indices)
            {
                if (index < 0 || index >= table.Count) continue;
                string name = table[index];
                if (FontNameRegex.IsMatch(name)) result.Add(name);
            }
            return result;
        }

        // ---- Contents: require the 03 10 00 00 tag + 04 C0 marker (excludes page/section names) ----
        internal static List<string> ParseContentsFontRecords(byte[] data)
        {
            var result = new List<string>();
            int n = data.Length, i = 4;
            while (i + 6 < n)
            {
                if (data[i] == 0x04 && data[i + 1] == 0xC0 &&
                    data[i - 4] == ContentsFontTag[0] && data[i - 3] == ContentsFontTag[1] &&
                    data[i - 2] == ContentsFontTag[2] && data[i - 1] == ContentsFontTag[3])
                {
                    uint totalLen = BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(data, i + 2, 4));
                    long nameBytes = (long)totalLen - 6; // minus null(2) + trailer(4)
                    if (nameBytes >= 2 && nameBytes <= 80 && nameBytes % 2 == 0 &&
                        i + 6 + nameBytes <= n)
                    {
                        string? name = DecodeFontName(data, i + 6, (int)nameBytes);
                        if (name != null)
                        {
                            result.Add(name);
                            i += (int)(6 + nameBytes);
                            continue;
                        }
                    }
                }
                i++;
            }
            return result;
        }

        private static string? DecodeFontName(byte[] data, int offset, int byteLen)
        {
            string s = Encoding.Unicode.GetString(data, offset, byteLen).TrimEnd('\0').Trim();
            return FontNameRegex.IsMatch(s) ? s : null;
        }

        private static string? ReadPrintableTag(byte[] data, int offset)
        {
            Span<char> chars = stackalloc char[4];
            for (int i = 0; i < 4; i++)
            {
                byte b = data[offset + i];
                if (b < 0x20 || b > 0x7E) return null;
                chars[i] = (char)b;
            }
            return new string(chars);
        }
    }
}

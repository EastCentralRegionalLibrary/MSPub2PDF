using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PublisherConverter.Core.FeaturesOnDemand
{
    /// <summary>
    /// Reads font family names out of TrueType/OpenType files without any
    /// platform font API, so the FoD pipeline can (a) register an extracted font
    /// under the right family name(s) and (b) decide which requested missing
    /// fonts a freshly-installed file actually satisfies.
    ///
    /// Both single-font files (<c>.ttf</c>, <c>.otf</c>) and TrueType Collections
    /// (<c>.ttc</c>) are handled: a collection's <c>ttcf</c> header is walked and
    /// every member font's <c>name</c> table is parsed, so a single
    /// <c>mingliu.ttc</c> correctly yields PMingLiU, MingLiU, MingLiU_HKSCS, …
    ///
    /// The parser is defensive — every offset is bounds-checked and any
    /// malformed structure yields the names gathered so far rather than throwing.
    /// </summary>
    public static class FontFileInspector
    {
        private const uint TagTtcf = 0x74746366; // 'ttcf'
        private const uint TagName = 0x6E616D65; // 'name'
        private const uint SfntTrueType = 0x00010000;
        private const uint SfntOtto = 0x4F54544F;     // 'OTTO'
        private const uint SfntTrue = 0x74727565;     // 'true'
        private const uint SfntTyp1 = 0x74797031;     // 'typ1'

        // name table IDs we care about.
        private const ushort NameIdFamily = 1;
        private const ushort NameIdTypographicFamily = 16;

        public static bool IsFontFile(string name)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            return ext == ".ttf" || ext == ".ttc" || ext == ".otf";
        }

        /// <summary>True for a TrueType Collection (.ttc / 'ttcf' magic).</summary>
        public static bool IsCollection(string name)
            => string.Equals(Path.GetExtension(name), ".ttc", StringComparison.OrdinalIgnoreCase);

        public static IReadOnlyList<string> GetFamilyNames(string path)
        {
            try
            {
                return GetFamilyNames(File.ReadAllBytes(path));
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        public static IReadOnlyList<string> GetFamilyNames(byte[] data)
        {
            var families = new List<string>();
            if (data == null || data.Length < 12) return families;

            try
            {
                uint tag = ReadU32(data, 0);
                if (tag == TagTtcf)
                {
                    uint numFonts = ReadU32(data, 8);
                    // Cap the count to something sane to avoid a malformed header
                    // sending us off the end of the buffer.
                    if (numFonts > 256) numFonts = 256;
                    for (uint i = 0; i < numFonts; i++)
                    {
                        int recordOffset = 12 + (int)(i * 4);
                        if (recordOffset + 4 > data.Length) break;
                        uint fontOffset = ReadU32(data, recordOffset);
                        AddFamiliesFromOffsetTable(data, (int)fontOffset, families);
                    }
                }
                else if (tag == SfntTrueType || tag == SfntOtto || tag == SfntTrue || tag == SfntTyp1)
                {
                    AddFamiliesFromOffsetTable(data, 0, families);
                }
            }
            catch
            {
                // Return whatever we managed to read.
            }

            return Dedupe(families);
        }

        private static void AddFamiliesFromOffsetTable(byte[] data, int sfntOffset, List<string> families)
        {
            if (sfntOffset < 0 || sfntOffset + 12 > data.Length) return;

            ushort numTables = ReadU16(data, sfntOffset + 4);
            int recordBase = sfntOffset + 12;
            for (int i = 0; i < numTables; i++)
            {
                int rec = recordBase + i * 16;
                if (rec + 16 > data.Length) return;
                uint tableTag = ReadU32(data, rec);
                if (tableTag != TagName) continue;

                uint tableOffset = ReadU32(data, rec + 8);
                uint tableLength = ReadU32(data, rec + 12);
                string? family = ParseNameTable(data, (int)tableOffset, (int)tableLength);
                if (!string.IsNullOrWhiteSpace(family)) families.Add(family!);
                return;
            }
        }

        private static string? ParseNameTable(byte[] data, int offset, int length)
        {
            if (offset < 0 || length < 6 || offset + 6 > data.Length) return null;
            int end = Math.Min(data.Length, offset + length);

            ushort count = ReadU16(data, offset + 2);
            ushort stringStorageRel = ReadU16(data, offset + 4);
            int storageBase = offset + stringStorageRel;

            string? best = null;
            int bestScore = int.MinValue;

            int recordsBase = offset + 6;
            for (int i = 0; i < count; i++)
            {
                int rec = recordsBase + i * 12;
                if (rec + 12 > end) break;

                ushort platformId = ReadU16(data, rec);
                ushort encodingId = ReadU16(data, rec + 2);
                ushort languageId = ReadU16(data, rec + 4);
                ushort nameId = ReadU16(data, rec + 6);
                ushort len = ReadU16(data, rec + 8);
                ushort strOff = ReadU16(data, rec + 10);

                if (nameId != NameIdFamily && nameId != NameIdTypographicFamily) continue;

                // Bound the string to the name table's own storage region, not the
                // whole file, so a bad offset cannot splice in bytes from another
                // table and yield a bogus family name.
                int strStart = storageBase + strOff;
                if (strStart < storageBase || strStart + len > end) continue;

                string? value = DecodeName(data, strStart, len, platformId, encodingId);
                if (string.IsNullOrWhiteSpace(value)) continue;

                int score = ScoreNameRecord(platformId, nameId, languageId);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = value;
                }
            }

            return best;
        }

        // Prefer the typographic family (16) over the legacy family (1), the
        // Windows platform over Mac, and English over other languages.
        private static int ScoreNameRecord(ushort platformId, ushort nameId, ushort languageId)
        {
            int score = 0;
            if (nameId == NameIdTypographicFamily) score += 1000;
            if (platformId == 3) score += 100;       // Windows
            else if (platformId == 0) score += 60;    // Unicode
            else if (platformId == 1) score += 30;    // Macintosh
            if (languageId == 0x0409 || languageId == 0) score += 10; // US English / Mac English
            return score;
        }

        private static string? DecodeName(byte[] data, int start, int len, ushort platformId, ushort encodingId)
        {
            try
            {
                // Windows (3) and Unicode (0) platforms use UTF-16BE. Mac (1)
                // Roman is decoded as Latin-1, which covers ASCII family names.
                if (platformId == 3 || platformId == 0)
                {
                    return Encoding.BigEndianUnicode.GetString(data, start, len).Trim('\0').Trim();
                }
                return Encoding.Latin1.GetString(data, start, len).Trim('\0').Trim();
            }
            catch
            {
                return null;
            }
        }

        private static List<string> Dedupe(List<string> names)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names)
            {
                if (string.IsNullOrWhiteSpace(n)) continue;
                if (seen.Add(n)) result.Add(n);
            }
            return result;
        }

        private static uint ReadU32(byte[] d, int o)
            => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

        private static ushort ReadU16(byte[] d, int o)
            => (ushort)((d[o] << 8) | d[o + 1]);
    }
}

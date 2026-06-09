using System;

namespace PublisherConverter.Core.FontSources
{
    /// <summary>Result of validating a candidate font payload.</summary>
    public sealed class IntegrityResult
    {
        public bool IsValidTtf { get; init; }
        public string? Reason { get; init; }

        public static IntegrityResult Valid { get; } = new IntegrityResult { IsValidTtf = true };
        public static IntegrityResult Invalid(string reason) => new IntegrityResult { IsValidTtf = false, Reason = reason };
    }

    /// <summary>
    /// Confirms a downloaded payload is a genuine, installable TrueType (.ttf):
    /// correct sfnt version, a sane table directory, and the TrueType outline
    /// table ('glyf'). OpenType/CFF ('OTTO'), WOFF/WOFF2, TrueType Collections
    /// ('ttcf'), and corrupt/empty/mislabeled files are rejected — so a payload
    /// that is really an .otf or a webfont with a .ttf name never installs.
    /// </summary>
    public static class FontIntegrityValidator
    {
        private const uint SfntTrueType = 0x00010000;
        private const uint SfntTrue = 0x74727565; // 'true'
        private const uint TagGlyf = 0x676C7966;   // 'glyf'
        private const uint TagHead = 0x68656164;   // 'head'
        private const uint TagName = 0x6E616D65;   // 'name'

        public static IntegrityResult Validate(byte[]? data)
        {
            if (data == null || data.Length < 12) return IntegrityResult.Invalid("payload empty or too small");

            uint sfnt = ReadU32(data, 0);
            switch (sfnt)
            {
                case 0x4F54544F: return IntegrityResult.Invalid("payload is OpenType/CFF (.otf), not .ttf");
                case 0x774F4646: return IntegrityResult.Invalid("payload is WOFF, not .ttf");
                case 0x774F4632: return IntegrityResult.Invalid("payload is WOFF2, not .ttf");
                case 0x74746366: return IntegrityResult.Invalid("payload is a TrueType Collection (.ttc), not a single .ttf");
            }
            if (sfnt != SfntTrueType && sfnt != SfntTrue)
                return IntegrityResult.Invalid($"unrecognized sfnt version 0x{sfnt:X8}");

            ushort numTables = ReadU16(data, 4);
            if (numTables == 0 || numTables > 4096) return IntegrityResult.Invalid("implausible table count");

            int dirEnd = 12 + numTables * 16;
            if (dirEnd > data.Length) return IntegrityResult.Invalid("truncated table directory");

            bool hasGlyf = false, hasHead = false, hasName = false;
            for (int i = 0; i < numTables; i++)
            {
                int rec = 12 + i * 16;
                uint tag = ReadU32(data, rec);
                uint offset = ReadU32(data, rec + 8);
                uint length = ReadU32(data, rec + 12);
                if (offset > (uint)data.Length || offset + length > (uint)data.Length)
                    return IntegrityResult.Invalid($"table 0x{tag:X8} extends past end of file");

                if (tag == TagGlyf) hasGlyf = true;
                else if (tag == TagHead) hasHead = true;
                else if (tag == TagName) hasName = true;
            }

            if (!hasHead) return IntegrityResult.Invalid("missing required 'head' table");
            if (!hasName) return IntegrityResult.Invalid("missing required 'name' table");
            if (!hasGlyf) return IntegrityResult.Invalid("missing 'glyf' table (not a TrueType-outline font)");

            return IntegrityResult.Valid;
        }

        private static uint ReadU32(byte[] d, int o)
            => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

        private static ushort ReadU16(byte[] d, int o)
            => (ushort)((d[o] << 8) | d[o + 1]);
    }
}

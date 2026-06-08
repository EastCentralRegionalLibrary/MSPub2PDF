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
    /// Extracts the font family names referenced by a Microsoft Publisher (.pub) publication
    /// WITHOUT opening it in Publisher.
    ///
    /// A .pub is an OLE2 / Compound File Binary document. Font references appear in two streams,
    /// each with its own layout. IMPORTANT: both layouts are SHARED by non-font tables (paragraph
    /// styles, page/section names), so naive record-walking misidentifies those as fonts. The
    /// parsing below disambiguates structurally:
    ///
    ///   Quill/QuillSub/CONTENTS  -- text-engine tables of [u16 len][UTF-16LE name][4-byte trailer].
    ///       The STYLE table and the FONT table share this exact shape. The font table is
    ///       identified by a fingerprint: it always contains the OS script-fallback block
    ///       (Mangal, Vrinda, Raavi, ...), which no other table does.
    ///
    ///   Contents                 -- per-shape / WordArt font references. A font record is the
    ///       byte tag 03 10 00 00 immediately followed by marker 04 C0, then [u32 totalLen]
    ///       [UTF-16LE name]. Page/section-name records use marker 0F C0 / 0E C0 and lack the
    ///       03 10 00 00 tag, so requiring the tag excludes them.
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

        // The OS script-fallback fonts Publisher always writes into the font table. Used purely
        // as a fingerprint to tell the font table apart from same-shaped style tables.
        private static readonly HashSet<string> FallbackFingerprint = new HashSet<string>(StringComparer.Ordinal)
        {
            "mangal", "vrinda", "raavi", "shruti", "kalinga", "latha", "gautami", "tunga", "kartika",
            "browalliaupc", "dokchampa", "microsofthimalaya", "malgungothic", "msmincho", "pmingliu",
            "simsun", "estrangeloedessa", "mvboli", "iskoolapota", "nyala", "plantagenetcherokee",
            "daunpenh", "mongolianbaiti", "eucrosiaupc", "batang", "angsananew", "tahoma", "sylfaen",
        };
        private const int FingerprintThreshold = 3;

        // 03 10 00 00 immediately precedes the 04 C0 marker on genuine Contents font records.
        private static readonly byte[] ContentsFontTag = { 0x03, 0x10, 0x00, 0x00 };

        private static readonly string[] QuillStreamPath = { "Quill", "QuillSub", "CONTENTS" };
        private static readonly string[] ContentsStreamPath = { "Contents" };

        public IReadOnlySet<string> ExtractFontNames(string pubPath)
        {
            var fonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            byte[]? quill = TryReadStream(pubPath, QuillStreamPath);
            if (quill != null)
            {
                foreach (var f in ParseQuillFontTable(quill))
                    fonts.Add(f);
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

        // ---- Quill: pick the [u16 len][name][4-trailer] chain that carries the font fingerprint ----
        internal static List<string> ParseQuillFontTable(byte[] data)
        {
            List<string> best = new List<string>();
            int bestScore = -1;

            foreach (var chain in EnumerateChains(data))
            {
                int score = 0;
                foreach (var f in chain)
                {
                    if (FallbackFingerprint.Contains(Normalize(f))) score++;
                }
                if (score > bestScore) { bestScore = score; best = chain; }
            }

            return bestScore >= FingerprintThreshold ? best : new List<string>();
        }

        private static IEnumerable<List<string>> EnumerateChains(byte[] data)
        {
            int n = data.Length, i = 0;
            while (i + 2 < n)
            {
                int j = i;
                var chain = new List<string>();
                while (j + 2 <= n)
                {
                    int len = data[j] | (data[j + 1] << 8);
                    if (len < 2 || len > 40) break;
                    int nameBytes = len * 2;
                    if (j + 2 + nameBytes > n) break;
                    string? name = DecodeFontName(data, j + 2, nameBytes);
                    if (name == null) break;
                    chain.Add(name);
                    j += 2 + nameBytes + 4; // name + 4-byte trailer
                }

                if (chain.Count >= 3) { yield return chain; i = j; }
                else i++;
            }
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

        private static string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c) || c == '-') continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core;
using PublisherConverter.Core.FontSources;
using PublisherConverter.Core.FontWorker;

namespace PublisherConverter.Tests.FontSources
{
    /// <summary>Builds a <see cref="ResolverContext"/> over a recording installer and a temp scratch dir.</summary>
    internal sealed class ResolverHarness : IDisposable
    {
        public FakeUserFontInstaller Installer { get; } = new FakeUserFontInstaller();
        public List<string> Log { get; } = new();
        public string ScratchDir { get; }

        public ResolverHarness()
        {
            ScratchDir = Path.Combine(Path.GetTempPath(), "fs-test-" + Guid.NewGuid().ToString("N"));
        }

        public ResolverContext Context() => new ResolverContext
        {
            Installer = Installer,
            ScratchDirectory = ScratchDir,
            Log = Log,
            CorrelationId = "test",
        };

        public void Dispose()
        {
            try { if (Directory.Exists(ScratchDir)) Directory.Delete(ScratchDir, true); } catch { }
        }
    }

    /// <summary>Fake Microsoft (Layer 1) batch resolver with configurable outcome.</summary>
    internal sealed class FakeInnerResolver : IFontResolver
    {
        public HashSet<string> ResolveThese { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Calls { get; private set; }

        public Task<FontResolutionOutcome> ResolveMissingFontsAsync(IReadOnlyList<string> missingFonts, CancellationToken cancellationToken)
        {
            Calls++;
            var resolved = missingFonts.Where(f => ResolveThese.Contains(f)).ToList();
            var still = missingFonts.Where(f => !ResolveThese.Contains(f)).ToList();
            return Task.FromResult(new FontResolutionOutcome
            {
                InitiallyMissing = missingFonts,
                Resolved = resolved,
                StillMissing = still,
                Log = new[] { "[fake-microsoft] ran" },
            });
        }
    }

    /// <summary>Scripted source resolver: per-font outcomes, call recording, optional throw.</summary>
    internal sealed class ScriptedSourceResolver : IFontSourceResolver
    {
        private readonly Func<FontRequest, FontAcquisitionResult> _script;
        public ResolutionLayer Layer { get; }
        public bool IsEnabled { get; set; } = true;
        public List<string> Seen { get; } = new();
        public Exception? ThrowFor { get; set; }
        public string? ThrowForFamily { get; set; }

        public ScriptedSourceResolver(ResolutionLayer layer, Func<FontRequest, FontAcquisitionResult> script)
        {
            Layer = layer;
            _script = script;
        }

        public Task<FontAcquisitionResult> TryResolveAsync(FontRequest request, ResolverContext context, CancellationToken cancellationToken)
        {
            Seen.Add(request.NormalizedFamily);
            if (ThrowFor != null && (ThrowForFamily == null || string.Equals(ThrowForFamily, request.NormalizedFamily, StringComparison.OrdinalIgnoreCase)))
                throw ThrowFor;
            return Task.FromResult(_script(request));
        }

        public static FontAcquisitionResult Installed(FontRequest r, ResolutionLayer layer) => new FontAcquisitionResult
        {
            RequestedFontName = r.RequestedName, NormalizedFamily = r.NormalizedFamily,
            Status = AcquisitionStatus.Installed, Layer = layer, SourceId = layer.ToString(), MatchConfidence = 0.9,
        };

        public static FontAcquisitionResult Miss(FontRequest r, ResolutionLayer layer) => FontAcquisitionResult.Miss(r, layer);

        public static FontAcquisitionResult Manual(FontRequest r, ResolutionLayer layer) => new FontAcquisitionResult
        {
            RequestedFontName = r.RequestedName, NormalizedFamily = r.NormalizedFamily,
            Status = AcquisitionStatus.ManualReviewRequired, Layer = layer, SourceId = layer.ToString(),
            License = LicenseStatus.ManualReviewRequired, ManualReviewRequired = true,
        };

        // Manual-review result carrying a real staged file the orchestrator can
        // install from once the user approves.
        public static FontAcquisitionResult ManualStaged(FontRequest r, ResolutionLayer layer, string stagedPath, string? licenseText = null, string sourceId = "dafont")
            => new FontAcquisitionResult
            {
                RequestedFontName = r.RequestedName, NormalizedFamily = r.NormalizedFamily,
                Status = AcquisitionStatus.ManualReviewRequired, Layer = layer, SourceId = sourceId,
                License = LicenseStatus.ManualReviewRequired, ManualReviewRequired = true,
                DownloadedFilePath = stagedPath, LicenseText = licenseText,
                FailureReason = "matched manual-review keyword 'free for personal use'",
            };
    }

    /// <summary>
    /// Fake <see cref="IFontHttpClient"/>. Seed probe existence, downloadable byte
    /// payloads, and string pages by URL; every call is recorded so tests can
    /// assert which URLs were touched (and that unrelated ones were not).
    /// </summary>
    internal sealed class FakeFontHttpClient : IFontHttpClient
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Pages { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TextFetchResult> DetailedResponses { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Existing { get; } = new(StringComparer.Ordinal);

        public List<string> Probed { get; } = new();
        public List<string> Downloaded { get; } = new();
        public List<string> Fetched { get; } = new();

        /// <summary>Request headers observed per GET/detailed-GET, in call order.</summary>
        public List<(string Url, IDictionary<string, string>? Headers)> FetchedRequests { get; } = new();

        public void SeedFile(string url, byte[] data) { Files[url] = data; Existing.Add(url); }
        public void SeedExists(string url) => Existing.Add(url);
        public void SeedPage(string url, string body) => Pages[url] = body;

        public void SeedResponse(string url, int statusCode, string? body = null, IDictionary<string, string>? headers = null)
            => DetailedResponses[url] = new TextFetchResult
            {
                StatusCode = statusCode,
                Body = body,
                Headers = new Dictionary<string, string>(headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            };

        public Task<ProbeResult> ProbeAsync(string url, IDictionary<string, string>? headers, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Probed.Add(url);
            bool exists = Existing.Contains(url) || Files.ContainsKey(url);
            long? len = Files.TryGetValue(url, out var b) ? b.Length : (long?)null;
            return Task.FromResult(exists ? new ProbeResult { Exists = true, ContentLength = len, StatusCode = 200 } : ProbeResult.Missing);
        }

        public Task<byte[]?> DownloadBytesAsync(string url, IDictionary<string, string>? headers, long maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Downloaded.Add(url);
            return Task.FromResult(Files.TryGetValue(url, out var b) ? b : null);
        }

        public Task<string?> GetStringAsync(string url, IDictionary<string, string>? headers, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Fetched.Add(url);
            FetchedRequests.Add((url, headers));
            return Task.FromResult(Pages.TryGetValue(url, out var s) ? s : null);
        }

        public Task<TextFetchResult?> GetStringDetailedAsync(string url, IDictionary<string, string>? headers, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Fetched.Add(url);
            FetchedRequests.Add((url, headers));
            if (DetailedResponses.TryGetValue(url, out var seeded)) return Task.FromResult<TextFetchResult?>(seeded);
            if (Pages.TryGetValue(url, out var s)) return Task.FromResult<TextFetchResult?>(new TextFetchResult { StatusCode = 200, Body = s });
            return Task.FromResult<TextFetchResult?>(null);
        }
    }

    /// <summary>Builds a structurally valid .ttf (head + name + glyf) carrying a family name.</summary>
    internal static class TtfTestBuilder
    {
        public static byte[] BuildValidTtf(string family)
        {
            byte[] name = BuildNameTable(family);
            byte[] head = new byte[54]; // contents irrelevant to the integrity check
            byte[] glyf = new byte[8];

            const int numTables = 3;
            int dataStart = 12 + numTables * 16;

            int glyfOff = dataStart;
            int headOff = glyfOff + glyf.Length;
            int nameOff = headOff + head.Length;

            using var ms = new MemoryStream();
            WriteU32(ms, 0x00010000); // sfnt version
            WriteU16(ms, numTables);
            WriteU16(ms, 0); WriteU16(ms, 0); WriteU16(ms, 0);

            WriteTableRecord(ms, 0x676C7966, glyfOff, glyf.Length); // 'glyf'
            WriteTableRecord(ms, 0x68656164, headOff, head.Length); // 'head'
            WriteTableRecord(ms, 0x6E616D65, nameOff, name.Length); // 'name'

            ms.Write(glyf, 0, glyf.Length);
            ms.Write(head, 0, head.Length);
            ms.Write(name, 0, name.Length);
            return ms.ToArray();
        }

        private static void WriteTableRecord(Stream s, uint tag, int offset, int length)
        {
            WriteU32(s, tag);
            WriteU32(s, 0); // checksum
            WriteU32(s, (uint)offset);
            WriteU32(s, (uint)length);
        }

        private static byte[] BuildNameTable(string family)
        {
            byte[] value = Encoding.BigEndianUnicode.GetBytes(family);
            int headerLen = 6 + 12;
            using var ms = new MemoryStream();
            WriteU16(ms, 0); WriteU16(ms, 1); WriteU16(ms, headerLen);
            WriteU16(ms, 3); WriteU16(ms, 1); WriteU16(ms, 0x0409); WriteU16(ms, 1);
            WriteU16(ms, value.Length); WriteU16(ms, 0);
            ms.Write(value, 0, value.Length);
            return ms.ToArray();
        }

        private static void WriteU16(Stream s, int v) { s.WriteByte((byte)((v >> 8) & 0xFF)); s.WriteByte((byte)(v & 0xFF)); }
        private static void WriteU32(Stream s, uint v)
        {
            s.WriteByte((byte)((v >> 24) & 0xFF)); s.WriteByte((byte)((v >> 16) & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF)); s.WriteByte((byte)(v & 0xFF));
        }
    }

    /// <summary>Builds an in-memory ZIP from name→bytes entries.</summary>
    internal static class ZipBuilder
    {
        public static byte[] Build(IEnumerable<(string Name, byte[] Data)> entries)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, data) in entries)
                {
                    var e = zip.CreateEntry(name);
                    using var es = e.Open();
                    es.Write(data, 0, data.Length);
                }
            }
            return ms.ToArray();
        }

        public static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);
    }
}

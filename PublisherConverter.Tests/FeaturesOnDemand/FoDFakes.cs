using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FeaturesOnDemand;

namespace PublisherConverter.Tests.FeaturesOnDemand
{
    /// <summary>
    /// Fake UUP Dump client: returns a canned update id and manifest, counts
    /// calls (so the "fetched once per batch" behaviour is assertable), and can
    /// be made to throw at either stage.
    /// </summary>
    internal sealed class FakeUupDumpClient : IUupDumpClient
    {
        public string UpdateId { get; set; } = "uuid-26100-amd64";
        public Dictionary<string, UupFile> Files { get; } = new(StringComparer.Ordinal);

        public int FindCalls { get; private set; }
        public int GetCalls { get; private set; }
        public Exception? FindThrows { get; set; }
        public Exception? GetThrows { get; set; }
        public List<string?> SeenCorrelationIds { get; } = new();

        public void AddPackage(string fileName, long size, string url, string? sha256 = null)
            => Files[fileName] = new UupFile { FileName = fileName, Size = size, Url = url, Sha256 = sha256 };

        public Task<string> FindLatestUpdateIdAsync(string buildSearch, string architecture, string? correlationId, CancellationToken cancellationToken)
        {
            FindCalls++;
            SeenCorrelationIds.Add(correlationId);
            if (FindThrows != null) throw FindThrows;
            return Task.FromResult(UpdateId);
        }

        public Task<IReadOnlyDictionary<string, UupFile>> GetFilesAsync(string updateId, string? correlationId, CancellationToken cancellationToken)
        {
            GetCalls++;
            if (GetThrows != null) throw GetThrows;
            return Task.FromResult<IReadOnlyDictionary<string, UupFile>>(Files);
        }
    }

    /// <summary>
    /// Fake FoD pipeline for FontManagementService integration tests. Records the
    /// batches handed to it and resolves a configured subset, so the coordinator's
    /// "route still-missing → FoD → merge" wiring can be asserted in isolation.
    /// </summary>
    internal sealed class FakeFoDPipeline : IFeaturesOnDemandFontPipeline
    {
        public List<IReadOnlyList<string>> Inputs { get; } = new();
        public HashSet<string> ResolveThese { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Exception? Throws { get; set; }
        public int Calls { get; private set; }

        public Task<FoDPipelineResult> RunAsync(
            IReadOnlyList<string> missingFonts,
            Core.IUserFontInstaller installer,
            FeaturesOnDemandOptions options,
            string? correlationId,
            IProgress<FoDProgress>? progress,
            CancellationToken cancellationToken)
        {
            Calls++;
            Inputs.Add(missingFonts.ToList());
            if (Throws != null) throw Throws;

            var resolved = missingFonts.Where(f => ResolveThese.Contains(f)).ToList();
            var still = missingFonts.Where(f => !ResolveThese.Contains(f)).ToList();
            return Task.FromResult(new FoDPipelineResult
            {
                Resolved = resolved,
                StillMissing = still,
                Log = new[] { "[fake-fod] ran" },
            });
        }
    }

    /// <summary>Fake signature verifier: trusted by default, with per-file overrides.</summary>
    internal sealed class FakeCabSignatureVerifier : ICabSignatureVerifier
    {
        public bool DefaultTrusted { get; set; } = true;
        public Dictionary<string, SignatureVerificationResult> ByFileName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Verified { get; } = new();

        public Task<SignatureVerificationResult> VerifyAsync(string filePath, string? correlationId, CancellationToken cancellationToken)
        {
            string leaf = Path.GetFileName(filePath);
            Verified.Add(leaf);
            if (ByFileName.TryGetValue(leaf, out var configured)) return Task.FromResult(configured);
            return Task.FromResult(DefaultTrusted
                ? SignatureVerificationResult.Trusted("CN=Test Signer")
                : SignatureVerificationResult.Untrusted("UntrustedSigner", "fake-untrusted"));
        }
    }

    /// <summary>Fake CAB extractor: materialises configured font files per CAB.</summary>
    internal sealed class FakeCabFontExtractor : ICabFontExtractor
    {
        public sealed record FakeFont(string FileName, string[] Families, bool IsCollection);

        public Dictionary<string, List<FakeFont>> ByCab { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> ExtractedCabs { get; } = new();
        public Exception? ThrowForCab { get; set; }

        public void Add(string cabFileName, string fontFileName, string[] families, bool isCollection = false)
        {
            if (!ByCab.TryGetValue(cabFileName, out var list))
            {
                list = new List<FakeFont>();
                ByCab[cabFileName] = list;
            }
            list.Add(new FakeFont(fontFileName, families, isCollection));
        }

        public IReadOnlyList<string> Enumerate(string cabPath)
        {
            string leaf = Path.GetFileName(cabPath);
            return ByCab.TryGetValue(leaf, out var fonts) ? fonts.Select(f => f.FileName).ToList() : new List<string>();
        }

        public Task<IReadOnlyList<ExtractedFont>> ExtractFontsAsync(string cabPath, string destinationDir, CancellationToken cancellationToken)
        {
            string leaf = Path.GetFileName(cabPath);
            ExtractedCabs.Add(leaf);
            if (ThrowForCab != null && leaf.Contains("boom", StringComparison.OrdinalIgnoreCase)) throw ThrowForCab;

            Directory.CreateDirectory(destinationDir);
            var result = new List<ExtractedFont>();
            if (ByCab.TryGetValue(leaf, out var fonts))
            {
                foreach (var f in fonts)
                {
                    string path = Path.Combine(destinationDir, f.FileName);
                    File.WriteAllBytes(path, new byte[] { 0x46, 0x4F, 0x44 }); // "FOD"
                    result.Add(new ExtractedFont
                    {
                        FilePath = path,
                        FileName = f.FileName,
                        FamilyNames = f.Families,
                        IsCollection = f.IsCollection,
                    });
                }
            }
            return Task.FromResult<IReadOnlyList<ExtractedFont>>(result);
        }
    }
}

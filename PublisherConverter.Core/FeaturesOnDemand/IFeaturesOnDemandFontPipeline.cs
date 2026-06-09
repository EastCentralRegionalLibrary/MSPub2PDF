using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FeaturesOnDemand
{
    /// <summary>Stage identifiers for FoD progress reporting.</summary>
    public enum FoDStage
    {
        Map,
        Resolve,
        Download,
        Verify,
        Extract,
        Install,
    }

    /// <summary>A progress tick from the FoD pipeline.</summary>
    public sealed class FoDProgress
    {
        public required FoDStage Stage { get; init; }
        public required string Message { get; init; }
        public int Completed { get; init; }
        public int Total { get; init; }
        public string? CorrelationId { get; init; }
    }

    /// <summary>Outcome of one FoD pipeline run over a batch of missing fonts.</summary>
    public sealed class FoDPipelineResult
    {
        public IReadOnlyList<string> Resolved { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> StillMissing { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();

        /// <summary>File names of CABs that failed verification and were quarantined.</summary>
        public IReadOnlyList<string> Quarantined { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// The Features-on-Demand fallback: given a batch of missing fonts, locate,
    /// download, verify, extract, and install the matching Microsoft
    /// LanguageFeatures font CABs. Processing is staged across the whole batch
    /// (map → resolve → download → verify/extract → install) rather than handled
    /// one font at a time, and a failure in any single language/package is
    /// isolated so the rest of the batch still completes.
    /// </summary>
    public interface IFeaturesOnDemandFontPipeline
    {
        Task<FoDPipelineResult> RunAsync(
            IReadOnlyList<string> missingFonts,
            IUserFontInstaller installer,
            FeaturesOnDemandOptions options,
            string? correlationId,
            IProgress<FoDProgress>? progress,
            CancellationToken cancellationToken);
    }
}

using System;
using System.Collections.Generic;

namespace PublisherConverter.Core.FontSources
{
    /// <summary>The fallback layer that produced (or attempted) an acquisition.</summary>
    public enum ResolutionLayer
    {
        None = 0,
        LocalCache,
        Scratch,
        FeaturesOnDemand,
        GoogleFonts,
        VendorRepo,
        Community,
    }

    /// <summary>Outcome of evaluating a font payload's license.</summary>
    public enum LicenseStatus
    {
        /// <summary>License not evaluated (e.g. trusted Microsoft/Google source).</summary>
        NotApplicable = 0,

        /// <summary>License is clearly permissive for redistribution/install.</summary>
        Allowed,

        /// <summary>License signals are missing/ambiguous — a human must decide.</summary>
        ManualReviewRequired,

        /// <summary>License clearly forbids install/redistribution.</summary>
        Rejected,
    }

    /// <summary>Terminal status of a per-font acquisition attempt.</summary>
    public enum AcquisitionStatus
    {
        /// <summary>A valid installable .ttf was acquired and installed.</summary>
        Installed = 0,

        /// <summary>A valid .ttf was acquired but install was not performed (e.g. caller staged only).</summary>
        Acquired,

        /// <summary>No layer produced a usable result.</summary>
        Missing,

        /// <summary>A candidate was found but its license requires manual review before install.</summary>
        ManualReviewRequired,

        /// <summary>A candidate was found but rejected (license or integrity).</summary>
        Rejected,
    }

    /// <summary>
    /// A normalized request for one font family/style, produced by
    /// <see cref="FontFamilyNormalizer"/> from a raw requested name.
    /// </summary>
    public sealed class FontRequest
    {
        /// <summary>The exact string the document asked for.</summary>
        public required string RequestedName { get; init; }

        /// <summary>Family name after normalization + alias resolution, style stripped.</summary>
        public required string NormalizedFamily { get; init; }

        /// <summary>Requested style tokens (e.g. "Bold", "Italic"), empty for Regular.</summary>
        public IReadOnlyList<string> RequestedStyles { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Structured per-font acquisition result. Carries everything the orchestrator,
    /// UI, and tests need to reason about an attempt — no nulls-as-signals.
    /// </summary>
    public sealed class FontAcquisitionResult
    {
        public required string RequestedFontName { get; init; }
        public required string NormalizedFamily { get; init; }
        public string? RequestedStyle { get; init; }

        public AcquisitionStatus Status { get; init; } = AcquisitionStatus.Missing;
        public ResolutionLayer Layer { get; init; } = ResolutionLayer.None;
        public string? SourceId { get; init; }
        public string? SourceUrl { get; init; }
        public string? DownloadedFilePath { get; init; }
        public string? InstalledFilePath { get; init; }
        public LicenseStatus License { get; init; } = LicenseStatus.NotApplicable;

        /// <summary>0..1 confidence that the acquired family matches the request.</summary>
        public double MatchConfidence { get; init; }

        public string? FailureReason { get; init; }
        public bool ManualReviewRequired { get; init; }

        /// <summary>True when the font is present after this attempt (installed or already local).</summary>
        public bool IsResolved => Status == AcquisitionStatus.Installed || Status == AcquisitionStatus.Acquired;

        public static FontAcquisitionResult Miss(FontRequest request, ResolutionLayer layer, string? reason = null)
            => new FontAcquisitionResult
            {
                RequestedFontName = request.RequestedName,
                NormalizedFamily = request.NormalizedFamily,
                Status = AcquisitionStatus.Missing,
                Layer = layer,
                FailureReason = reason,
            };
    }
}

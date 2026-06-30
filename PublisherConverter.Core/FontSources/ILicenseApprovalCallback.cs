using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FontSources
{
    /// <summary>
    /// Details surfaced to the user when a community/non-Microsoft font candidate
    /// needs manual license review before it can be installed.
    /// </summary>
    public sealed class LicenseApprovalRequest
    {
        /// <summary>The font name as the document referenced it.</summary>
        public required string FontName { get; init; }

        /// <summary>The source that found the candidate (e.g. "dafont").</summary>
        public required string SourceId { get; init; }

        /// <summary>Download URL of the archive or font file.</summary>
        public string? SourceUrl { get; init; }

        /// <summary>
        /// Human-readable reason from the license evaluator
        /// (e.g. "matched manual-review keyword 'free for personal use'").
        /// </summary>
        public required string LicenseReason { get; init; }

        /// <summary>Raw text of the license file extracted from the archive.</summary>
        public string? LicenseText { get; init; }

        /// <summary>
        /// Filesystem path of the staged .ttf file in scratch. Install from here
        /// if the user approves.
        /// </summary>
        public required string StagedFilePath { get; init; }
    }

    /// <summary>
    /// Called when a font candidate requires manual license review before install.
    /// Return true to approve (proceed with install from StagedFilePath), false to decline.
    /// Must be UI-thread-safe; the orchestrator awaits it.
    /// </summary>
    public delegate Task<bool> LicenseApprovalCallback(
        LicenseApprovalRequest request, CancellationToken cancellationToken);

    /// <summary>One candidate offered to the user when a search is ambiguous.</summary>
    public sealed class DisambiguationCandidate
    {
        public required string Slug { get; init; }
        public required string SourceId { get; init; }
        public required double Confidence { get; init; }
    }

    /// <summary>
    /// Called when a search returns multiple candidates above the confidence threshold.
    /// Return the index of the chosen candidate, or -1 to cancel acquisition.
    /// </summary>
    public delegate Task<int> DisambiguationCallback(
        string requestedFontName,
        IReadOnlyList<DisambiguationCandidate> candidates,
        CancellationToken cancellationToken);
}

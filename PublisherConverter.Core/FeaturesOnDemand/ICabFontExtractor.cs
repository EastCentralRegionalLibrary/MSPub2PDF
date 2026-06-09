using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FeaturesOnDemand
{
    /// <summary>A font file pulled out of a CAB, with its parsed family names.</summary>
    public sealed class ExtractedFont
    {
        public required string FilePath { get; init; }
        public required string FileName { get; init; }

        /// <summary>
        /// Family names contained in the file. A <c>.ttc</c> collection yields
        /// more than one (e.g. PMingLiU, MingLiU, MingLiU_HKSCS).
        /// </summary>
        public IReadOnlyList<string> FamilyNames { get; init; } = Array.Empty<string>();

        public bool IsCollection { get; init; }
    }

    /// <summary>
    /// Enumerates a CAB and extracts only the font files it contains
    /// (<c>.ttf</c>, <c>.ttc</c>, <c>.otf</c>), leaving everything else on disk
    /// untouched. Implementations differ only in <em>how</em> they decompress —
    /// the orchestration depends solely on this seam and is faked in tests.
    /// </summary>
    public interface ICabFontExtractor
    {
        /// <summary>Lists every file name in the cabinet (no extraction).</summary>
        IReadOnlyList<string> Enumerate(string cabPath);

        /// <summary>
        /// Extracts the font files from <paramref name="cabPath"/> into
        /// <paramref name="destinationDir"/>. Per-file failures are isolated:
        /// a single bad entry is skipped, not allowed to abort the rest.
        /// </summary>
        Task<IReadOnlyList<ExtractedFont>> ExtractFontsAsync(string cabPath, string destinationDir, CancellationToken cancellationToken);
    }
}

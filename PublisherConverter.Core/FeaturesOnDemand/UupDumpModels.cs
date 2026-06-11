using System;
using System.Collections.Generic;

namespace PublisherConverter.Core.FeaturesOnDemand
{
    /// <summary>
    /// One entry from the UUP Dump <c>get.php</c> file manifest. The manifest is
    /// a JSON object keyed by file name; this is the (normalized) value plus the
    /// key copied onto <see cref="FileName"/> for convenience.
    ///
    /// The upstream <c>size</c> field is sometimes a JSON number and sometimes a
    /// string, so the client parses it leniently into <see cref="Size"/>. The
    /// download URL can appear under several field names (mirroring the Python
    /// reference's <c>extract_download_url</c>); the first non-empty one wins and
    /// is surfaced on <see cref="Url"/>.
    /// </summary>
    public sealed class UupFile
    {
        public string FileName { get; init; } = string.Empty;
        public long Size { get; init; }
        public string? Url { get; init; }
        public string? Sha256 { get; init; }
        public string? Sha1 { get; init; }
    }

    /// <summary>
    /// A single build returned by <c>listid.php</c>. Only the fields the resolver
    /// needs are modelled; everything else in the response is ignored.
    /// </summary>
    public sealed class UupBuild
    {
        public string? Uuid { get; init; }
        public string? Title { get; init; }
        public string? Build { get; init; }
        public string? Arch { get; init; }
    }

    /// <summary>
    /// A font CAB resolved for one language token: where to download it, how big
    /// it is, and the digests advertised by the manifest (used for an integrity
    /// pre-check before the heavier signature verification).
    /// </summary>
    public sealed class ResolvedFontPackage
    {
        /// <summary>Language / script token, e.g. "Thai", "Jpan", "Hant", "Cans".</summary>
        public required string Language { get; init; }

        /// <summary>CAB file name as it appears in the manifest.</summary>
        public required string FileName { get; init; }

        /// <summary>Direct download URL extracted from the manifest entry.</summary>
        public required string DownloadUrl { get; init; }

        public long SizeBytes { get; init; }
        public string? Sha256 { get; init; }
        public string? Sha1 { get; init; }

        /// <summary>Architecture the package was restricted to (default "amd64").</summary>
        public required string Architecture { get; init; }

        /// <summary>Update UUID the package was resolved from.</summary>
        public required string UpdateId { get; init; }

        public override string ToString()
            => $"{FileName} ({SizeBytes / (1024.0 * 1024.0):F2} MiB, {Architecture})";
    }

    /// <summary>
    /// Tunables for the Features-on-Demand fallback pipeline. Defaults mirror the
    /// Python reference (<c>--build-search 26100</c>, amd64).
    /// </summary>
    public sealed class FeaturesOnDemandOptions
    {
        /// <summary>Build search text passed to <c>listid.php</c>. Default "26100".</summary>
        public string BuildSearch { get; init; } = "26100";

        /// <summary>Target architecture for both build selection and package filtering. Default "amd64".</summary>
        public string Architecture { get; init; } = "amd64";

        /// <summary>
        /// Scratch directory CABs are downloaded into and fonts are extracted
        /// from. A per-run sub-directory is created beneath it. Defaults to a
        /// folder under the system temp path.
        /// </summary>
        public string? ScratchDirectory { get; init; }

        /// <summary>Maximum number of CABs downloaded concurrently. Default 3.</summary>
        public int MaxDownloadConcurrency { get; init; } = 3;

        /// <summary>Maximum number of CABs verified/extracted concurrently. Default 2.</summary>
        public int MaxExtractConcurrency { get; init; } = 2;

        public static FeaturesOnDemandOptions Default { get; } = new FeaturesOnDemandOptions();

        internal string ResolveScratchRoot()
        {
            if (!string.IsNullOrWhiteSpace(ScratchDirectory)) return ScratchDirectory!;
            return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MSPub2PDF", "fod-scratch");
        }

        internal int SafeDownloadConcurrency => MaxDownloadConcurrency > 0 ? MaxDownloadConcurrency : 1;
        internal int SafeExtractConcurrency => MaxExtractConcurrency > 0 ? MaxExtractConcurrency : 1;
    }
}

using System;
using System.Collections.Generic;

namespace PublisherConverter.Core
{
    public enum MigrationStatus
    {
        Pending,
        Staged,
        VerifiedComplete,
        VerifiedWithWarnings,
        FailedIngress,
        FailedConversion,
        FailedEgress
    }

    /// <summary>
    /// Thrown by ConverterEngine when too many consecutive files fail and the
    /// circuit breaker halts the batch. Derives from InvalidOperationException
    /// for backwards compatibility with existing catch blocks. Callers (the GUI)
    /// can catch this specifically to offer the user a "Continue" option that
    /// re-runs while skipping the already-attempted files.
    /// </summary>
    public class CircuitBreakerTrippedException : InvalidOperationException
    {
        public int ConsecutiveFailures { get; }
        public IReadOnlyList<string> AttemptedSourcePaths { get; }

        public CircuitBreakerTrippedException(int consecutiveFailures, IReadOnlyList<string> attemptedSourcePaths)
            : base($"Circuit breaker tripped after {consecutiveFailures} consecutive failures. Aborting batch.")
        {
            ConsecutiveFailures = consecutiveFailures;
            AttemptedSourcePaths = attemptedSourcePaths;
        }
    }

    public class FileRecord
    {
        public string FileName { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string OriginalFullPath { get; set; } = string.Empty;
        public string LocalPubPath { get; set; } = string.Empty;
        public string LocalPdfPath { get; set; } = string.Empty;
        public string FinalPdfPath { get; set; } = string.Empty;
        public DateTime CreationTime { get; set; }
        public DateTime LastWriteTime { get; set; }
        public long SourceSizeLength { get; set; }
        public string SourceHash { get; set; } = "N/A";
        public string OutputHash { get; set; } = "N/A";
        public bool HasMacros { get; set; } = false;
        public DateTime ProcessedAtUtc { get; set; }
        public MigrationStatus Status { get; set; } = MigrationStatus.Pending;
        public string Details { get; set; } = "Pending processing.";
        public int MissingAssetsCount { get; set; } = 0;
        public string MissingAssetsList { get; set; } = "None";

        /// <summary>
        /// Populated by the font pre-flight check when the document refers
        /// to fonts not installed on the local system. When non-zero, the
        /// document is rejected before it ever reaches Publisher to avoid
        /// the export-time crash.
        /// </summary>
        public int MissingFontsCount { get; set; } = 0;
        public string MissingFontsList { get; set; } = "None";

        /// <summary>
        /// True when the engine had to retry — i.e. at least one render
        /// attempt for this document threw an export error, timed out, or
        /// looked like a Publisher crash. Used to keep the source .pub file
        /// out of the auto-delete pass even if the final attempt succeeded,
        /// so the user can review what changed.
        /// </summary>
        public bool HadFailedAttempt { get; set; } = false;
    }

    public class ProgressReport
    {
        public int TotalFiles { get; set; }
        public int ProcessedFiles { get; set; }
        public int SuccessCount { get; set; }
        public int WarningCount { get; set; }
        public int FailureCount { get; set; }
        public string CurrentActionMessage { get; set; } = string.Empty;
        public FileRecord? CurrentFile { get; set; }
    }

    public class ConversionOptions
    {
        public string SourcePath { get; set; } = string.Empty;
        public string ArchivePath { get; set; } = string.Empty;
        public bool RunLinkCheck { get; set; } = true;
        public string? ManifestOutputPath { get; set; }
        public bool CompressArchive { get; set; } = false;
        public bool DeleteSourceOnSuccess { get; set; } = false;

        /// <summary>
        /// When true, the worker process is recycled (restarted) every
        /// <see cref="ProcessRecycleInterval"/> successfully converted files to
        /// keep COM/Publisher memory in check. Disabled by default.
        /// </summary>
        public bool EnableProcessRecycle { get; set; } = false;
        public int ProcessRecycleInterval { get; set; } = 50;

        /// <summary>
        /// When true, the batch halts (throwing <see cref="CircuitBreakerTrippedException"/>)
        /// once <see cref="MaxConsecutiveFailures"/> files fail back-to-back. The GUI
        /// then offers a "Continue" option. Disabled by default — failures are
        /// recorded per-file and the run proceeds through the whole set.
        /// </summary>
        public bool EnableCircuitBreaker { get; set; } = false;
        public int MaxConsecutiveFailures { get; set; } = 3;

        public int FileTimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// When true, the orchestrator hands the per-file missing-font list
        /// to <see cref="IFontResolver"/> before failing the document so the
        /// resolver can attempt a best-effort install (Windows optional-font
        /// capability and/or downloadable fallback per FontMapping.json).
        /// Defaults to false — pre-flight rejects unresolved fonts as before.
        /// </summary>
        public bool EnableAutoFontInstallation { get; set; } = false;

        /// <summary>
        /// When true, files with still-missing fonts (after any resolution
        /// attempt) are sent to the renderer anyway instead of being rejected.
        /// Missing-font diagnostics on the FileRecord and in the
        /// FontPreflightReport are preserved either way. Defaults to false —
        /// pre-flight blocks the file as before.
        /// </summary>
        public bool OverrideFontSkip { get; set; } = false;

        /// <summary>
        /// Absolute source paths to skip during discovery. The GUI's "Continue"
        /// flow populates this with files attempted in a prior run so resuming
        /// after a circuit-breaker trip processes only the remaining work.
        /// Case-insensitive matching is recommended for Windows path semantics.
        /// </summary>
        public HashSet<string>? SkipSourcePaths { get; set; }
    }
}

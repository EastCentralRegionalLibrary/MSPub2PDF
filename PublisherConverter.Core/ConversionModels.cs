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
        public int ProcessRecycleInterval { get; set; } = 50;
        public int FileTimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// Absolute source paths to skip during discovery. The GUI's "Continue"
        /// flow populates this with files attempted in a prior run so resuming
        /// after a circuit-breaker trip processes only the remaining work.
        /// Case-insensitive matching is recommended for Windows path semantics.
        /// </summary>
        public HashSet<string>? SkipSourcePaths { get; set; }
    }
}

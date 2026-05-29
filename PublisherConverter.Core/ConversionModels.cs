using System;

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
        public bool CompressArchive { get; set; } = false;
        public bool DeleteSourceOnSuccess { get; set; } = false;
        public int ProcessRecycleInterval { get; set; } = 50;
        public int FileTimeoutSeconds { get; set; } = 60;
    }
}
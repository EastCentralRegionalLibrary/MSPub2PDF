using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    public interface IPublisherRenderer
    {
        void Initialize();
        void Shutdown();
        Task ExecuteRenderingJobAsync(FileRecord record, string sourcePubPath, string targetPdfPath, bool runLinkCheck, int timeoutSeconds, CancellationToken cancellationToken);
        void Recycle();
        bool RecordBatchFailure();
        void RecordBatchSuccess();
        int ConsecutiveFailures { get; }
    }

    public interface IFileInspector
    {
        FileSafetyStatus InspectFile(string filePath);
    }

    public interface IArchiveService : IDisposable
    {
        void Initialize();
        void StageFile(string sourceFullPath, string relativePath, string fileName);
        void FinalizeArchive();
        bool Compress { get; }
    }

    public interface IHashProvider
    {
        string GetSha256Hash(string filePath);
    }

    public interface IProgressReporter
    {
        void Report(ProgressReport report);
    }

    public interface IManifestWriter
    {
        void WriteManifest(string directory, List<FileRecord> records);
    }
}

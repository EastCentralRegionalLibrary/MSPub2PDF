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

    public interface IPublisherWorkerClient : IDisposable
    {
        Task<WorkerResponse> SendRequestAsync(WorkerRequest request, int? timeoutSeconds, CancellationToken cancellationToken);
        void EnsureWorkerStarted();
        void RecycleWorker();
        bool IsHealthy { get; }
    }

    public interface IWorkerTransport : IDisposable
    {
        Task SendRequestAsync(WorkerRequest request, CancellationToken cancellationToken);
        Task<WorkerResponse> ReceiveResponseAsync(CancellationToken cancellationToken);
        void Connect();
    }

    public interface IProcessLauncher
    {
        IProcessHandle StartWorker();
    }

    public interface IProcessHandle : IDisposable
    {
        int Id { get; }
        bool HasExited { get; }
        void Kill();
        event EventHandler Exited;
    }

    public interface IWorkerHealthMonitor
    {
        bool IsProcessHealthy(IProcessHandle? handle);
    }

    public interface ITimeoutProvider
    {
        TimeSpan GetTimeout(string command);
    }
}

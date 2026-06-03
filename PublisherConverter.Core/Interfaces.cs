using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    public interface IPublisherRenderer
    {
        void Initialize();
        Task InitializeAsync(CancellationToken cancellationToken);
        void Shutdown();
        Task ExecuteRenderingJobAsync(
            FileRecord record,
            string sourcePubPath,
            string targetPdfPath,
            bool runLinkCheck,
            RenderIntent intent,
            bool docStructureTags,
            int timeoutSeconds,
            CancellationToken cancellationToken);
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
        Task EnsureWorkerStartedAsync(CancellationToken cancellationToken);
        void RecycleWorker();
        bool IsHealthy { get; }
    }

    public interface IWorkerTransport : IDisposable
    {
        Task SendRequestAsync(WorkerRequest request, CancellationToken cancellationToken);
        Task<WorkerResponse> ReceiveResponseAsync(CancellationToken cancellationToken);
        void Connect();
        Task ConnectAsync(CancellationToken cancellationToken, int? timeoutMs = null);
    }

    /// <summary>
    /// Renders a Publisher document to PDF. Implementations may be COM-based
    /// (PublisherComRenderer) or stub-based (used by integration tests).
    /// </summary>
    public interface IDocumentRenderer : IDisposable
    {
        void Initialize();
        RenderResult Render(RenderJob job);

        /// <summary>
        /// Raised asynchronously when the renderer detects that its underlying
        /// rendering engine has died (e.g., Publisher's mspub.exe terminated
        /// out from under us). The worker host subscribes so it can tear down
        /// immediately instead of waiting for the in-flight Render call to
        /// time out — a stuck COM RPC will not unblock on its own.
        /// </summary>
        event EventHandler? EngineCrashed;
    }

    public interface IProcessLauncher
    {
        IProcessHandle StartWorker(string pipeName);
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

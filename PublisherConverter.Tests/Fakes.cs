using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core;

namespace PublisherConverter.Tests
{
    public class FakeFileInspector : IFileInspector
    {
        public Queue<Func<string, FileSafetyStatus>> InspectBehaviors { get; } = new Queue<Func<string, FileSafetyStatus>>();
        public FileSafetyStatus InspectFile(string filePath)
        {
            if (InspectBehaviors.Count > 0) return InspectBehaviors.Dequeue()(filePath);
            return new FileSafetyStatus { Reason = "Clean" };
        }
    }

    public class FakePublisherRenderer : IPublisherRenderer
    {
        public int RenderCount { get; private set; }
        public int RecycleCount { get; private set; }
        public int ConsecutiveFailures { get; private set; }
        public int MaxConsecutiveFailures { get; set; } = 3;
        public bool ShutdownCalled { get; private set; }
        public Queue<Func<FileRecord, Task>> RenderBehaviors { get; } = new Queue<Func<FileRecord, Task>>();

        public void Initialize() { }
        public async Task InitializeAsync(CancellationToken cancellationToken) { await Task.CompletedTask; }
        public void Shutdown() { ShutdownCalled = true; }

        public async Task ExecuteRenderingJobAsync(FileRecord record, string sourcePubPath, string targetPdfPath, bool runLinkCheck, int timeoutSeconds, CancellationToken cancellationToken)
        {
            RenderCount++;
            if (RenderBehaviors.Count > 0)
            {
                await RenderBehaviors.Dequeue()(record);
                return;
            }

            // Default behavior: create a dummy PDF file
            if (!File.Exists(targetPdfPath))
            {
                string dir = Path.GetDirectoryName(targetPdfPath)!;
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(targetPdfPath, "%PDF-1.4\n%Fake PDF content");
            }
        }

        public void Recycle()
        {
            RecycleCount++;
        }

        public bool RecordBatchFailure()
        {
            ConsecutiveFailures++;
            return ConsecutiveFailures >= MaxConsecutiveFailures;
        }

        public void RecordBatchSuccess()
        {
            ConsecutiveFailures = 0;
        }
    }

    public class FakeArchiveService : IArchiveService
    {
        public bool Initialized { get; private set; }
        public List<string> StagedFiles { get; } = new List<string>();
        public bool Finalized { get; private set; }
        public bool Disposed { get; private set; }
        public bool Compress { get; set; }

        public void Initialize() => Initialized = true;
        public void StageFile(string sourceFullPath, string relativePath, string fileName) => StagedFiles.Add(Path.Combine(relativePath, fileName));
        public void FinalizeArchive() => Finalized = true;
        public void Dispose() => Disposed = true;
    }

    public class FakeManifestWriter : IManifestWriter
    {
        public string? WrittenDirectory { get; private set; }
        public List<FileRecord>? WrittenRecords { get; private set; }
        public bool ThrowOnWrite { get; set; }

        public void WriteManifest(string directory, List<FileRecord> records)
        {
            if (ThrowOnWrite) throw new UnauthorizedAccessException("Simulated manifest write failure");
            WrittenDirectory = directory;
            WrittenRecords = new List<FileRecord>(records);
        }
    }

    public class FakeProcessLauncher : IProcessLauncher
    {
        public List<FakeProcessHandle> CreatedProcesses { get; } = new List<FakeProcessHandle>();
        public List<string> RequestedPipeNames { get; } = new List<string>();

        public IProcessHandle StartWorker(string pipeName)
        {
            RequestedPipeNames.Add(pipeName);
            var handle = new FakeProcessHandle(CreatedProcesses.Count + 1);
            CreatedProcesses.Add(handle);
            return handle;
        }
    }

    public class FakeProcessHandle : IProcessHandle
    {
        public int Id { get; }
        public bool HasExited { get; set; }
        public bool Killed { get; private set; }
        public event EventHandler? Exited;

        public FakeProcessHandle(int id) => Id = id;

        public void Kill()
        {
            Killed = true;
            HasExited = true;
            Exited?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() { }
    }

    public class FakeWorkerTransport : IWorkerTransport
    {
        public Queue<WorkerResponse> Responses { get; } = new Queue<WorkerResponse>();
        public List<WorkerRequest> SentRequests { get; } = new List<WorkerRequest>();
        public bool Connected { get; private set; }
        public bool Disposed { get; private set; }

        public void Connect() => Connected = true;

        public Task ConnectAsync(CancellationToken cancellationToken, int? timeoutMs = null)
        {
            Connected = true;
            return Task.CompletedTask;
        }

        public Task SendRequestAsync(WorkerRequest request, CancellationToken cancellationToken)
        {
            SentRequests.Add(request);
            return Task.CompletedTask;
        }

        public virtual Task<WorkerResponse> ReceiveResponseAsync(CancellationToken cancellationToken)
        {
            if (Responses.Count > 0) return Task.FromResult(Responses.Dequeue());
            return Task.FromResult(new WorkerResponse { Success = true });
        }

        public void Dispose() => Disposed = true;
    }

    public class FakeWorkerHealthMonitor : IWorkerHealthMonitor
    {
        public bool IsHealthy { get; set; } = true;
        public bool IsProcessHealthy(IProcessHandle? handle) => IsHealthy && handle != null && !handle.HasExited;
    }

    public class FakeTimeoutProvider : ITimeoutProvider
    {
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
        public TimeSpan GetTimeout(string command) => Timeout;
    }
}

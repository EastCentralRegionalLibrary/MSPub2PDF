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
        public FileSafetyStatus InspectFile(string filePath)
        {
            return new FileSafetyStatus { Reason = "Clean" };
        }
    }

    public class FakePublisherRenderer : IPublisherRenderer
    {
        public int RenderCount { get; private set; }
        public int RecycleCount { get; private set; }
        public int ConsecutiveFailures { get; private set; }
        public int MaxConsecutiveFailures { get; set; } = 3;
        public Queue<Func<FileRecord, Task>> RenderBehaviors { get; } = new Queue<Func<FileRecord, Task>>();

        public void Initialize() { }
        public void Shutdown() { }

        public async Task ExecuteRenderingJobAsync(FileRecord record, string sourcePubPath, string targetPdfPath, bool runLinkCheck, int timeoutSeconds, CancellationToken cancellationToken)
        {
            RenderCount++;
            if (RenderBehaviors.Count > 0)
            {
                await RenderBehaviors.Dequeue()(record);
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

        public void WriteManifest(string directory, List<FileRecord> records)
        {
            WrittenDirectory = directory;
            WrittenRecords = new List<FileRecord>(records);
        }
    }
}

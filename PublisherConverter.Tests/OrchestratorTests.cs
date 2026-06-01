using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using PublisherConverter.Core;

namespace PublisherConverter.Tests
{
    public class OrchestratorTests : IDisposable
    {
        private readonly string _testWorkspaceDir;
        private readonly FakePublisherRenderer _renderer = new FakePublisherRenderer();
        private readonly FakeFileInspector _inspector = new FakeFileInspector();
        private readonly HashProvider _hashProvider = new HashProvider();
        private readonly FakeManifestWriter _manifestWriter = new FakeManifestWriter();
        private readonly List<FakeArchiveService> _archiveServices = new List<FakeArchiveService>();

        public OrchestratorTests()
        {
            _testWorkspaceDir = Path.Combine(Path.GetTempPath(), $"OrchTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testWorkspaceDir);
        }

        private ConverterEngine CreateEngine()
        {
            return new ConverterEngine(
                _inspector,
                _hashProvider,
                _manifestWriter,
                _renderer,
                (path, compress) => {
                    var svc = new FakeArchiveService { Compress = compress };
                    _archiveServices.Add(svc);
                    return svc;
                }
            );
        }

        [Fact]
        public async Task Orchestrator_ShouldProcessFilesEndToEnd()
        {
            // Arrange
            string sourceDir = Path.Combine(_testWorkspaceDir, "Source");
            Directory.CreateDirectory(sourceDir);
            string filePath = Path.Combine(sourceDir, "test1.pub");
            File.WriteAllText(filePath, "fake contents");

            var options = new ConversionOptions { SourcePath = sourceDir };
            var engine = CreateEngine();

            // Act
            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            // Assert
            Assert.Equal(1, _renderer.RenderCount);
            Assert.NotNull(_manifestWriter.WrittenRecords);
            Assert.Single(_manifestWriter.WrittenRecords);
            Assert.Equal(MigrationStatus.VerifiedComplete, _manifestWriter.WrittenRecords[0].Status);
            // Since it's local, LocalPdfPath = FinalPdfPath = path with .pdf
            Assert.True(File.Exists(Path.Combine(sourceDir, "test1.pdf")));
        }

        [Fact]
        public async Task Orchestrator_ShouldRecycleRenderer()
        {
            // Arrange
            string sourceDir = Path.Combine(_testWorkspaceDir, "SourceRecycle");
            Directory.CreateDirectory(sourceDir);
            for (int i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(sourceDir, $"test{i}.pub"), "fake content");
            }

            var options = new ConversionOptions { SourcePath = sourceDir, ProcessRecycleInterval = 2 };
            var engine = CreateEngine();

            // Act
            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            // Assert
            Assert.Equal(2, _renderer.RecycleCount); // Recycles after 2nd and 4th file
        }

        [Fact]
        public async Task Orchestrator_ShouldBackupExistingPdf()
        {
            // Arrange
            string sourceDir = Path.Combine(_testWorkspaceDir, "SourceBackup");
            Directory.CreateDirectory(sourceDir);
            string pubFile = Path.Combine(sourceDir, "test.pub");
            string pdfFile = Path.Combine(sourceDir, "test.pdf");
            File.WriteAllText(pubFile, "fake content");
            File.WriteAllText(pdfFile, "%PDF-1.4\nExisting");

            var options = new ConversionOptions { SourcePath = sourceDir };
            var engine = CreateEngine();

            // Act
            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            // Assert
            Assert.True(File.Exists(pdfFile + ".old"));
            Assert.Contains("Fake PDF content", File.ReadAllText(pdfFile));
        }

        [Fact]
        public async Task Orchestrator_ShouldHandleCircuitBreaker()
        {
            // Arrange
            string sourceDir = Path.Combine(_testWorkspaceDir, "SourceCB");
            Directory.CreateDirectory(sourceDir);
            for (int i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(sourceDir, $"test{i}.pub"), "fake content");
            }

            // We need to use exactly 3 files to trip the breaker (default 3)
            // Each file will be attempted 3 times.
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    _renderer.RenderBehaviors.Enqueue(_ => throw new Exception($"Fail {i} attempt {j}"));
                }
            }

            var options = new ConversionOptions { SourcePath = sourceDir };
            var engine = CreateEngine();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None));
            Assert.Contains("Circuit breaker tripped", ex.Message);

            Assert.Equal(9, _renderer.RenderCount); // 3 files * 3 attempts
            Assert.NotNull(_manifestWriter.WrittenRecords);
            Assert.Equal(5, _manifestWriter.WrittenRecords.Count); // Total discovered

            // First 3 should be failed
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(MigrationStatus.FailedConversion, _manifestWriter.WrittenRecords[i].Status);
            }
            // Last 2 should be pending
            for (int i = 3; i < 5; i++)
            {
                Assert.Equal(MigrationStatus.Pending, _manifestWriter.WrittenRecords[i].Status);
            }
        }

        private class FakeProgressReporter : IProgressReporter
        {
            public void Report(ProgressReport report) { }
        }

        public void Dispose()
        {
            if (Directory.Exists(_testWorkspaceDir))
            {
                try { Directory.Delete(_testWorkspaceDir, true); } catch { }
            }
        }
    }
}

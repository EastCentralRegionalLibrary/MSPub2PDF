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
        public async Task Orchestrator_ShouldHandleManifestWritabilityFailure()
        {
            // Arrange
            string sourceDir = Path.Combine(_testWorkspaceDir, "SourceManifestFail");
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "test.pub"), "fake content");

            _manifestWriter.ThrowOnWrite = true;

            var options = new ConversionOptions { SourcePath = sourceDir };
            var engine = CreateEngine();

            // Act
            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            // Assert
            Assert.True(_renderer.ShutdownCalled);
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
        public async Task Orchestrator_ShouldBackupAndRestoreExistingPdf()
        {
            // Arrange
            string sourceDir = Path.Combine(_testWorkspaceDir, "SourceBackup");
            Directory.CreateDirectory(sourceDir);
            string pubFile = Path.Combine(sourceDir, "test.pub");
            string pdfFile = Path.Combine(sourceDir, "test.pdf");
            File.WriteAllText(pubFile, "fake content");
            File.WriteAllText(pdfFile, "%PDF-1.4\nExisting");

            // Script renderer to "succeed" but then simulate egress failure
            // To simulate egress failure, we'll script renderer to NOT create the PDF
            _renderer.RenderBehaviors.Enqueue(async r => {
                // Do nothing, hollow render
                await Task.CompletedTask;
            });

            var options = new ConversionOptions { SourcePath = sourceDir };
            var engine = CreateEngine();

            // Act
            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            // Assert
            Assert.Equal(MigrationStatus.FailedEgress, _manifestWriter.WrittenRecords![0].Status);
            Assert.True(File.Exists(pdfFile));
            Assert.Contains("Existing", File.ReadAllText(pdfFile));
            Assert.False(File.Exists(pdfFile + ".old"));
        }

        [Fact]
        public async Task Orchestrator_ShouldHandleHollowRenders()
        {
            // Arrange
            string sourceDir = Path.Combine(_testWorkspaceDir, "SourceHollow");
            Directory.CreateDirectory(sourceDir);
            for (int i = 0; i < 3; i++)
            {
                File.WriteAllText(Path.Combine(sourceDir, $"hollow{i}.pub"), "fake content");
            }

            // Script renderer to "succeed" but write an invalid (0-byte) PDF
            _renderer.RenderBehaviors.Enqueue(async r => {
                await File.WriteAllTextAsync(r.LocalPdfPath, "");
            });
            _renderer.RenderBehaviors.Enqueue(async r => {
                await File.WriteAllTextAsync(r.LocalPdfPath, "");
            });
            _renderer.RenderBehaviors.Enqueue(async r => {
                await File.WriteAllTextAsync(r.LocalPdfPath, "");
            });

            var options = new ConversionOptions { SourcePath = sourceDir };
            var engine = CreateEngine();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<CircuitBreakerTrippedException>(() => engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None));
            Assert.Contains("Circuit breaker tripped", ex.Message);

            Assert.NotNull(_manifestWriter.WrittenRecords);
            Assert.All(_manifestWriter.WrittenRecords.GetRange(0, 3), r => Assert.Equal(MigrationStatus.FailedEgress, r.Status));
        }

        [Fact]
        public async Task Orchestrator_ShouldTripBreakerOnHollowRenders()
        {
            // Arrange
            string sourceDir = Path.Combine(_testWorkspaceDir, "SourceTripHollow");
            Directory.CreateDirectory(sourceDir);
            for (int i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(sourceDir, $"hollow{i}.pub"), "fake content");
            }

            // Script renderer to "succeed" but write an invalid (0-byte) PDF for 3 files
            // Engine should trip breaker on the 3rd one.
            for (int i = 0; i < 3; i++)
            {
                _renderer.RenderBehaviors.Enqueue(async r => {
                    await File.WriteAllTextAsync(r.LocalPdfPath, "");
                });
            }

            var options = new ConversionOptions { SourcePath = sourceDir };
            var engine = CreateEngine();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<CircuitBreakerTrippedException>(() => engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None));
            Assert.Contains("Circuit breaker tripped", ex.Message);

            Assert.NotNull(_manifestWriter.WrittenRecords);
            Assert.Equal(5, _manifestWriter.WrittenRecords.Count);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(MigrationStatus.FailedEgress, _manifestWriter.WrittenRecords[i].Status);
                Assert.NotEqual(default(DateTime), _manifestWriter.WrittenRecords[i].ProcessedAtUtc);
            }
        }

        [Fact]
        public async Task Orchestrator_ShouldSetProcessedAtUtcOnAllPaths()
        {
            // Arrange
            string sourceDir = Path.Combine(_testWorkspaceDir, "SourceProcessedAt");
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "corrupt.pub"), "not ole"); // Will fail static triage
            File.WriteAllText(Path.Combine(sourceDir, "breaker.pub"), "fake content"); // Will trip breaker

            _inspector.InspectBehaviors.Enqueue(_ => new FileSafetyStatus { IsCorruptedOrInvalid = true, Reason = "Corrupt" });
            _renderer.RenderBehaviors.Enqueue(_ => throw new Exception("Tripped"));
            _renderer.RenderBehaviors.Enqueue(_ => throw new Exception("Tripped retry 1"));
            _renderer.RenderBehaviors.Enqueue(_ => throw new Exception("Tripped retry 2"));
            _renderer.MaxConsecutiveFailures = 1; // Trip on first file failure

            var options = new ConversionOptions { SourcePath = sourceDir };
            var engine = CreateEngine();

            // Act
            await Assert.ThrowsAsync<CircuitBreakerTrippedException>(() => engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None));

            // Assert
            Assert.NotNull(_manifestWriter.WrittenRecords);
            var corruptRecord = _manifestWriter.WrittenRecords.Find(r => r.FileName == "corrupt.pub");
            var breakerRecord = _manifestWriter.WrittenRecords.Find(r => r.FileName == "breaker.pub");

            Assert.NotNull(corruptRecord);
            Assert.NotEqual(default(DateTime), corruptRecord.ProcessedAtUtc);
            Assert.Equal(MigrationStatus.FailedConversion, corruptRecord.Status);

            Assert.NotNull(breakerRecord);
            Assert.NotEqual(default(DateTime), breakerRecord.ProcessedAtUtc);
            Assert.Equal(MigrationStatus.FailedConversion, breakerRecord.Status);
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
            var ex = await Assert.ThrowsAsync<CircuitBreakerTrippedException>(() => engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None));
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

        [Fact]
        public async Task Orchestrator_ShouldSkipFilesInSkipSourcePaths()
        {
            // Arrange
            string sourceDir = Path.Combine(_testWorkspaceDir, "SourceSkip");
            Directory.CreateDirectory(sourceDir);
            string keptPath = Path.Combine(sourceDir, "keep.pub");
            string skippedPath = Path.Combine(sourceDir, "skip.pub");
            File.WriteAllText(keptPath, "fake content");
            File.WriteAllText(skippedPath, "fake content");

            var options = new ConversionOptions
            {
                SourcePath = sourceDir,
                SkipSourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { skippedPath }
            };
            var engine = CreateEngine();

            // Act
            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            // Assert
            Assert.Equal(1, _renderer.RenderCount);
            Assert.NotNull(_manifestWriter.WrittenRecords);
            Assert.Single(_manifestWriter.WrittenRecords);
            Assert.Equal("keep.pub", _manifestWriter.WrittenRecords[0].FileName);
        }

        [Fact]
        public async Task Orchestrator_CircuitBreakerExceptionExposesAttemptedPaths()
        {
            // Arrange — 5 files, force the breaker to trip on the 3rd file.
            string sourceDir = Path.Combine(_testWorkspaceDir, "SourceBreakerPaths");
            Directory.CreateDirectory(sourceDir);
            for (int i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(sourceDir, $"f{i}.pub"), "fake content");
            }
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    _renderer.RenderBehaviors.Enqueue(_ => throw new Exception($"Fail {i}/{j}"));
                }
            }

            var options = new ConversionOptions { SourcePath = sourceDir };
            var engine = CreateEngine();

            // Act & Assert
            var ex = await Assert.ThrowsAsync<CircuitBreakerTrippedException>(() =>
                engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None));

            Assert.Equal(3, ex.ConsecutiveFailures);
            Assert.Equal(3, ex.AttemptedSourcePaths.Count);
            foreach (var p in ex.AttemptedSourcePaths)
            {
                Assert.StartsWith(sourceDir, p);
            }
        }

        [Fact]
        public async Task Orchestrator_ResumingWithSkipPathsProcessesOnlyRemainingFiles()
        {
            // Arrange — simulate "continue after circuit breaker". First run
            // trips after 3 failures; second run skips those 3 and processes
            // the remaining 2 successfully.
            string sourceDir = Path.Combine(_testWorkspaceDir, "SourceResume");
            Directory.CreateDirectory(sourceDir);
            for (int i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(sourceDir, $"f{i}.pub"), "fake content");
            }
            for (int i = 0; i < 9; i++) // 3 files × 3 retries
            {
                _renderer.RenderBehaviors.Enqueue(_ => throw new Exception("first run failure"));
            }

            var firstOptions = new ConversionOptions { SourcePath = sourceDir };
            var engine = CreateEngine();
            var ex = await Assert.ThrowsAsync<CircuitBreakerTrippedException>(() =>
                engine.ExecuteMigrationAsync(firstOptions, new FakeProgressReporter(), CancellationToken.None));

            // Act — second pass with the attempted paths as the skip list.
            var secondOptions = new ConversionOptions
            {
                SourcePath = sourceDir,
                SkipSourcePaths = new HashSet<string>(ex.AttemptedSourcePaths, StringComparer.OrdinalIgnoreCase)
            };
            await engine.ExecuteMigrationAsync(secondOptions, new FakeProgressReporter(), CancellationToken.None);

            // Assert
            Assert.NotNull(_manifestWriter.WrittenRecords);
            Assert.Equal(2, _manifestWriter.WrittenRecords.Count);
            Assert.All(_manifestWriter.WrittenRecords, r => Assert.Equal(MigrationStatus.VerifiedComplete, r.Status));
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

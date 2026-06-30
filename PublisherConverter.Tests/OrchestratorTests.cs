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

        private ConverterEngine CreateEngine(IFontAuditor? fontAuditor = null, IFontResolver? fontResolver = null)
        {
            return new ConverterEngine(
                _inspector,
                _hashProvider,
                _manifestWriter,
                _renderer,
                fontAuditor ?? new NoOpFontAuditor(),
                fontResolver ?? new NoOpFontResolver(),
                (path, compress) => {
                    var svc = new FakeArchiveService { Compress = compress };
                    _archiveServices.Add(svc);
                    return svc;
                }
            );
        }

        [Fact]
        public void Dispose_DisposesRenderer()
        {
            var engine = CreateEngine();

            engine.Dispose();

            Assert.True(_renderer.ShutdownCalled);
            Assert.Equal(1, _renderer.ShutdownCount);

            // Idempotent: a second Dispose does not shut the renderer down again.
            engine.Dispose();
            Assert.Equal(1, _renderer.ShutdownCount);
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

            var options = new ConversionOptions { SourcePath = sourceDir, EnableProcessRecycle = true, ProcessRecycleInterval = 2 };
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

            var options = new ConversionOptions { SourcePath = sourceDir, EnableCircuitBreaker = true, MaxConsecutiveFailures = 3 };
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

            var options = new ConversionOptions { SourcePath = sourceDir, EnableCircuitBreaker = true, MaxConsecutiveFailures = 3 };
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

            var options = new ConversionOptions { SourcePath = sourceDir, EnableCircuitBreaker = true, MaxConsecutiveFailures = 1 };
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

            var options = new ConversionOptions { SourcePath = sourceDir, EnableCircuitBreaker = true, MaxConsecutiveFailures = 3 };
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

            var options = new ConversionOptions { SourcePath = sourceDir, EnableCircuitBreaker = true, MaxConsecutiveFailures = 3 };
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

            var firstOptions = new ConversionOptions { SourcePath = sourceDir, EnableCircuitBreaker = true, MaxConsecutiveFailures = 3 };
            var engine = CreateEngine();
            var ex = await Assert.ThrowsAsync<CircuitBreakerTrippedException>(() =>
                engine.ExecuteMigrationAsync(firstOptions, new FakeProgressReporter(), CancellationToken.None));

            // Act — second pass with the attempted paths as the skip list.
            var secondOptions = new ConversionOptions
            {
                SourcePath = sourceDir,
                EnableCircuitBreaker = true,
                MaxConsecutiveFailures = 3,
                SkipSourcePaths = new HashSet<string>(ex.AttemptedSourcePaths, StringComparer.OrdinalIgnoreCase)
            };
            await engine.ExecuteMigrationAsync(secondOptions, new FakeProgressReporter(), CancellationToken.None);

            // Assert
            Assert.NotNull(_manifestWriter.WrittenRecords);
            Assert.Equal(2, _manifestWriter.WrittenRecords.Count);
            Assert.All(_manifestWriter.WrittenRecords, r => Assert.Equal(MigrationStatus.VerifiedComplete, r.Status));
        }

        [Fact]
        public async Task Orchestrator_DisabledCircuitBreaker_ProcessesAllFilesWithoutHalting()
        {
            // Arrange — 5 files that all fail rendering. With the breaker
            // disabled (the default), the run must NOT halt; every file should
            // be attempted and recorded as failed.
            string sourceDir = Path.Combine(_testWorkspaceDir, "SourceNoBreaker");
            Directory.CreateDirectory(sourceDir);
            for (int i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(sourceDir, $"f{i}.pub"), "fake content");
            }
            for (int i = 0; i < 15; i++) // 5 files × 3 retries
            {
                _renderer.RenderBehaviors.Enqueue(_ => throw new Exception("always fails"));
            }

            var options = new ConversionOptions { SourcePath = sourceDir, EnableCircuitBreaker = false };
            var engine = CreateEngine();

            // Act — should complete without throwing.
            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            // Assert
            Assert.Equal(15, _renderer.RenderCount);
            Assert.NotNull(_manifestWriter.WrittenRecords);
            Assert.Equal(5, _manifestWriter.WrittenRecords.Count);
            Assert.All(_manifestWriter.WrittenRecords, r => Assert.Equal(MigrationStatus.FailedConversion, r.Status));
        }

        [Fact]
        public async Task Orchestrator_DisabledRecycle_DoesNotRecycleEvenPastInterval()
        {
            // Arrange — 5 files that convert fine, recycle interval of 2 but
            // recycling disabled (the default). The engine must never recycle.
            string sourceDir = Path.Combine(_testWorkspaceDir, "SourceNoRecycle");
            Directory.CreateDirectory(sourceDir);
            for (int i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(sourceDir, $"f{i}.pub"), "fake content");
            }

            var options = new ConversionOptions { SourcePath = sourceDir, EnableProcessRecycle = false, ProcessRecycleInterval = 2 };
            var engine = CreateEngine();

            // Act
            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            // Assert
            Assert.Equal(0, _renderer.RecycleCount);
        }

        [Fact]
        public async Task Orchestrator_ExportFailure_StepsDownIntent()
        {
            // Arrange — single file. First attempt throws RenderExportFailureException;
            // engine should retry with the next-lower intent (Printing).
            string sourceDir = Path.Combine(_testWorkspaceDir, "SrcIntentStep");
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "a.pub"), "fake");

            _renderer.RenderBehaviors.Enqueue(_ => throw new RenderExportFailureException("export bombed"));
            // 2nd attempt: succeed (default behavior writes a PDF at targetPath)

            var options = new ConversionOptions { SourcePath = sourceDir };
            var engine = CreateEngine();

            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            Assert.Equal(2, _renderer.Attempts.Count);
            Assert.Equal(RenderIntent.Commercial, _renderer.Attempts[0].Intent);
            Assert.Equal(RenderIntent.Printing, _renderer.Attempts[1].Intent);

            // First attempt's target had no suffix; second attempt's did.
            Assert.DoesNotContain("_printres", _renderer.Attempts[0].TargetPdfPath);
            Assert.Contains("_printres", _renderer.Attempts[1].TargetPdfPath);

            // Final PDF on disk uses the suffixed name.
            Assert.True(File.Exists(Path.Combine(sourceDir, "a_printres.pdf")));
            Assert.False(File.Exists(Path.Combine(sourceDir, "a.pdf")));

            Assert.NotNull(_manifestWriter.WrittenRecords);
            var record = _manifestWriter.WrittenRecords[0];
            Assert.Equal(MigrationStatus.VerifiedComplete, record.Status);
            Assert.True(record.HadFailedAttempt);
        }

        [Fact]
        public async Task Orchestrator_TwoExportFailures_StepsDownToStandard()
        {
            string sourceDir = Path.Combine(_testWorkspaceDir, "SrcStandard");
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "doc.pub"), "fake");

            _renderer.RenderBehaviors.Enqueue(_ => throw new RenderExportFailureException("first bombed"));
            _renderer.RenderBehaviors.Enqueue(_ => throw new RenderExportFailureException("second bombed"));
            // 3rd attempt succeeds with Standard.

            var options = new ConversionOptions { SourcePath = sourceDir };
            var engine = CreateEngine();

            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            Assert.Equal(3, _renderer.Attempts.Count);
            Assert.Equal(RenderIntent.Commercial, _renderer.Attempts[0].Intent);
            Assert.Equal(RenderIntent.Printing,   _renderer.Attempts[1].Intent);
            Assert.Equal(RenderIntent.Standard,   _renderer.Attempts[2].Intent);

            Assert.True(File.Exists(Path.Combine(sourceDir, "doc_standardres.pdf")));
        }

        [Fact]
        public async Task Orchestrator_TwoCrashes_TurnsOffDocStructureTagsOnThirdAttempt()
        {
            string sourceDir = Path.Combine(_testWorkspaceDir, "SrcCrashTags");
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "fragile.pub"), "fake");

            _renderer.RenderBehaviors.Enqueue(_ => throw new RenderEngineCrashException("Publisher crashed"));
            _renderer.RenderBehaviors.Enqueue(_ => throw new RenderEngineCrashException("Publisher crashed again"));
            // 3rd attempt succeeds — no behavior queued.

            var options = new ConversionOptions { SourcePath = sourceDir };
            var engine = CreateEngine();

            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            Assert.Equal(3, _renderer.Attempts.Count);
            Assert.True(_renderer.Attempts[0].DocStructureTags);
            Assert.True(_renderer.Attempts[1].DocStructureTags);
            Assert.False(_renderer.Attempts[2].DocStructureTags);

            // Crashes don't step intent down per spec — only export errors do.
            Assert.All(_renderer.Attempts, a => Assert.Equal(RenderIntent.Commercial, a.Intent));

            Assert.True(File.Exists(Path.Combine(sourceDir, "fragile_notags.pdf")));
        }

        [Fact]
        public async Task Orchestrator_SuccessOnFirstTry_NoSuffixAndCanBeDeleted()
        {
            string sourceDir = Path.Combine(_testWorkspaceDir, "SrcCleanDelete");
            Directory.CreateDirectory(sourceDir);
            string pubFile = Path.Combine(sourceDir, "clean.pub");
            File.WriteAllText(pubFile, "fake");

            var options = new ConversionOptions { SourcePath = sourceDir, DeleteSourceOnSuccess = true };
            var engine = CreateEngine();

            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            Assert.Single(_renderer.Attempts);
            Assert.True(File.Exists(Path.Combine(sourceDir, "clean.pdf")));
            Assert.False(File.Exists(pubFile)); // deleted

            Assert.NotNull(_manifestWriter.WrittenRecords);
            Assert.False(_manifestWriter.WrittenRecords[0].HadFailedAttempt);
        }

        [Fact]
        public async Task Orchestrator_DegradedSuccess_KeepsSourceEvenWithDeleteOptionOn()
        {
            // A document that needed a retry (any error mode) must NOT have its
            // source deleted, even though it eventually converted, so the user
            // can review what changed.
            string sourceDir = Path.Combine(_testWorkspaceDir, "SrcDegradedKeep");
            Directory.CreateDirectory(sourceDir);
            string pubFile = Path.Combine(sourceDir, "retry.pub");
            File.WriteAllText(pubFile, "fake");

            _renderer.RenderBehaviors.Enqueue(_ => throw new RenderExportFailureException("needs retry"));
            // 2nd attempt: succeeds.

            var options = new ConversionOptions { SourcePath = sourceDir, DeleteSourceOnSuccess = true };
            var engine = CreateEngine();

            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            Assert.True(File.Exists(pubFile)); // kept
            Assert.True(File.Exists(Path.Combine(sourceDir, "retry_printres.pdf")));
        }

        [Fact]
        public async Task Orchestrator_DegradationDoesNotBleedAcrossDocuments()
        {
            // File 1 needs to step down to Standard. File 2 must start fresh
            // at Commercial — state must reset between documents.
            string sourceDir = Path.Combine(_testWorkspaceDir, "SrcIsolation");
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(sourceDir, "a.pub"), "fake");
            File.WriteAllText(Path.Combine(sourceDir, "b.pub"), "fake");

            // File 1: two export errors, then succeeds at Standard.
            _renderer.RenderBehaviors.Enqueue(_ => throw new RenderExportFailureException("a1"));
            _renderer.RenderBehaviors.Enqueue(_ => throw new RenderExportFailureException("a2"));
            // File 1's third attempt: default (success at Standard).
            // File 2: succeeds first try at Commercial.

            var options = new ConversionOptions { SourcePath = sourceDir };
            var engine = CreateEngine();

            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            // File 1 had 3 attempts; file 2 had 1.
            Assert.Equal(4, _renderer.Attempts.Count);

            // Last-attempted file 1: Standard. The first attempt of file 2 is
            // also in the list — it must be Commercial, with tags on.
            var fileBAttempts = _renderer.Attempts.FindAll(a => a.FileName == "b.pub");
            Assert.Single(fileBAttempts);
            Assert.Equal(RenderIntent.Commercial, fileBAttempts[0].Intent);
            Assert.True(fileBAttempts[0].DocStructureTags);
        }

        [Fact]
        public async Task Orchestrator_FontPreflight_SkipsRenderingAndAggregatesReport()
        {
            // Two files: "missing.pub" references a font that's missing
            // locally; "clean.pub" doesn't. The first must be rejected
            // before reaching the renderer; the second must convert normally.
            string sourceDir = Path.Combine(_testWorkspaceDir, "SrcFontPreflight");
            Directory.CreateDirectory(sourceDir);
            string missingPath = Path.Combine(sourceDir, "missing.pub");
            string cleanPath = Path.Combine(sourceDir, "clean.pub");
            File.WriteAllText(missingPath, "fake content");
            File.WriteAllText(cleanPath, "fake content");

            var auditor = new FakeFontAuditor();
            auditor.MissingByPath[missingPath] = new[] { "Lemon Cookie Bold", "New York" };

            var capturedLogLines = new List<string>();
            var capturingReporter = new CapturingProgressReporter(capturedLogLines);

            var options = new ConversionOptions { SourcePath = sourceDir };
            var engine = CreateEngine(auditor);

            // Act
            await engine.ExecuteMigrationAsync(options, capturingReporter, CancellationToken.None);

            // Only the clean file should have reached the renderer.
            Assert.Equal(1, _renderer.RenderCount);
            Assert.NotNull(_manifestWriter.WrittenRecords);
            Assert.Equal(2, _manifestWriter.WrittenRecords.Count);

            var missingRecord = _manifestWriter.WrittenRecords.Find(r => r.FileName == "missing.pub");
            Assert.NotNull(missingRecord);
            Assert.Equal(MigrationStatus.FailedConversion, missingRecord.Status);
            Assert.Equal(2, missingRecord.MissingFontsCount);
            Assert.Equal("Lemon Cookie Bold | New York", missingRecord.MissingFontsList);
            Assert.Contains("missing system font", missingRecord.Details);

            var cleanRecord = _manifestWriter.WrittenRecords.Find(r => r.FileName == "clean.pub");
            Assert.NotNull(cleanRecord);
            Assert.Equal(MigrationStatus.VerifiedComplete, cleanRecord.Status);
            Assert.Equal(0, cleanRecord.MissingFontsCount);

            // The end-of-run summary should appear in the log, grouped by font,
            // with the affected file path listed.
            Assert.Contains(capturedLogLines, l => l.Contains("Font pre-flight summary"));
            Assert.Contains(capturedLogLines, l => l.Contains("Lemon Cookie Bold"));
            Assert.Contains(capturedLogLines, l => l.Contains("New York"));
            Assert.Contains(capturedLogLines, l => l.Contains(missingPath));

            // The report is also exposed programmatically for the future auto-installer.
            var report = engine.LatestFontPreflightReport;
            Assert.True(report.HasFindings);
            Assert.Equal(2, report.DistinctMissingFontCount);
            Assert.Contains(missingPath, report.FilesByMissingFont["Lemon Cookie Bold"]);
            Assert.Contains(missingPath, report.FilesByMissingFont["New York"]);
        }

        [Fact]
        public async Task Orchestrator_FontPreflight_FailedFileIsNotDeletedEvenWithDeleteOption()
        {
            string sourceDir = Path.Combine(_testWorkspaceDir, "SrcFontPreflightKeep");
            Directory.CreateDirectory(sourceDir);
            string p = Path.Combine(sourceDir, "fragile.pub");
            File.WriteAllText(p, "fake content");

            var auditor = new FakeFontAuditor();
            auditor.MissingByPath[p] = new[] { "Missing Font" };

            var options = new ConversionOptions { SourcePath = sourceDir, DeleteSourceOnSuccess = true };
            var engine = CreateEngine(auditor);

            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            Assert.True(File.Exists(p), "Source file should not be deleted when the pre-flight rejects it.");
        }

        [Fact]
        public async Task Orchestrator_FontPreflight_ReportResetsBetweenRuns()
        {
            // Engines are reused across "Continue" runs; LatestFontPreflightReport
            // should reflect only the latest run, not accumulate forever.
            string sourceDir = Path.Combine(_testWorkspaceDir, "SrcFontReset");
            Directory.CreateDirectory(sourceDir);
            string p = Path.Combine(sourceDir, "a.pub");
            File.WriteAllText(p, "fake");

            var auditor = new FakeFontAuditor();
            auditor.MissingByPath[p] = new[] { "Ghost Font" };

            var options = new ConversionOptions { SourcePath = sourceDir };
            var engine = CreateEngine(auditor);

            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);
            Assert.True(engine.LatestFontPreflightReport.HasFindings);

            // Second run: no missing fonts for this path now.
            auditor.MissingByPath.Clear();
            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);
            Assert.False(engine.LatestFontPreflightReport.HasFindings);
        }

        [Fact]
        public async Task AutoResolve_Off_OverrideOff_FailsLikeBefore()
        {
            string sourceDir = Path.Combine(_testWorkspaceDir, "AutoOffOverrideOff");
            Directory.CreateDirectory(sourceDir);
            string p = Path.Combine(sourceDir, "x.pub");
            File.WriteAllText(p, "fake");

            var auditor = new FakeFontAuditor();
            auditor.MissingByPath[p] = new[] { "Lemon Cookie Bold" };
            var resolver = new FakeFontResolver();

            var options = new ConversionOptions
            {
                SourcePath = sourceDir,
                EnableAutoFontInstallation = false,
                OverrideFontSkip = false,
            };
            var engine = CreateEngine(auditor, resolver);

            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            Assert.Equal(0, resolver.CallCount);
            Assert.Equal(0, _renderer.RenderCount);
            Assert.Equal(MigrationStatus.FailedConversion, _manifestWriter.WrittenRecords![0].Status);
            Assert.Equal("Lemon Cookie Bold", _manifestWriter.WrittenRecords[0].MissingFontsList);
        }

        [Fact]
        public async Task AutoResolve_On_ResolverSucceeds_FileConvertsCleanly()
        {
            string sourceDir = Path.Combine(_testWorkspaceDir, "AutoOnSuccess");
            Directory.CreateDirectory(sourceDir);
            string p = Path.Combine(sourceDir, "x.pub");
            File.WriteAllText(p, "fake");

            var auditor = new FakeFontAuditor();
            auditor.MissingByPath[p] = new[] { "Mangal", "PMingLiU" };
            var resolver = new FakeFontResolver();
            resolver.ResolveOutcomes["Mangal"] = true;
            resolver.ResolveOutcomes["PMingLiU"] = true;

            var options = new ConversionOptions
            {
                SourcePath = sourceDir,
                EnableAutoFontInstallation = true,
                OverrideFontSkip = false,
            };
            var engine = CreateEngine(auditor, resolver);

            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            Assert.Equal(1, resolver.CallCount);
            Assert.Equal(1, _renderer.RenderCount);

            var record = _manifestWriter.WrittenRecords![0];
            Assert.Equal(MigrationStatus.VerifiedComplete, record.Status);
            Assert.Equal(0, record.MissingFontsCount);
            Assert.Equal("None", record.MissingFontsList);

            // Fully resolved + clean run → the preflight report should be empty.
            Assert.False(engine.LatestFontPreflightReport.HasFindings);
        }

        [Fact]
        public async Task AutoResolve_On_ResolverPartialFails_OverrideOff_StillRejectsRemaining()
        {
            string sourceDir = Path.Combine(_testWorkspaceDir, "AutoOnPartial");
            Directory.CreateDirectory(sourceDir);
            string p = Path.Combine(sourceDir, "x.pub");
            File.WriteAllText(p, "fake");

            var auditor = new FakeFontAuditor();
            auditor.MissingByPath[p] = new[] { "Mangal", "Lemon Cookie Bold" };
            var resolver = new FakeFontResolver();
            resolver.ResolveOutcomes["Mangal"] = true;
            resolver.ResolveOutcomes["Lemon Cookie Bold"] = false;

            var options = new ConversionOptions
            {
                SourcePath = sourceDir,
                EnableAutoFontInstallation = true,
                OverrideFontSkip = false,
            };
            var engine = CreateEngine(auditor, resolver);

            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            Assert.Equal(0, _renderer.RenderCount);

            var record = _manifestWriter.WrittenRecords![0];
            Assert.Equal(MigrationStatus.FailedConversion, record.Status);
            // Only the still-missing font remains in the diagnostic.
            Assert.Equal(1, record.MissingFontsCount);
            Assert.Equal("Lemon Cookie Bold", record.MissingFontsList);

            // The end-of-run report shows only what couldn't be resolved.
            var report = engine.LatestFontPreflightReport;
            Assert.True(report.HasFindings);
            Assert.False(report.FilesByMissingFont.ContainsKey("Mangal"));
            Assert.Contains(p, report.FilesByMissingFont["Lemon Cookie Bold"]);
        }

        [Fact]
        public async Task AutoResolve_Off_Override_On_RendersWithMissingFontsAndPreservesDiagnostics()
        {
            string sourceDir = Path.Combine(_testWorkspaceDir, "AutoOffOverrideOn");
            Directory.CreateDirectory(sourceDir);
            string p = Path.Combine(sourceDir, "x.pub");
            File.WriteAllText(p, "fake");

            var auditor = new FakeFontAuditor();
            auditor.MissingByPath[p] = new[] { "Lemon Cookie Bold", "New York" };
            var resolver = new FakeFontResolver(); // never called

            var capturedLog = new List<string>();
            var options = new ConversionOptions
            {
                SourcePath = sourceDir,
                EnableAutoFontInstallation = false,
                OverrideFontSkip = true,
            };
            var engine = CreateEngine(auditor, resolver);

            await engine.ExecuteMigrationAsync(options, new CapturingProgressReporter(capturedLog), CancellationToken.None);

            Assert.Equal(0, resolver.CallCount);
            Assert.Equal(1, _renderer.RenderCount); // got rendered

            var record = _manifestWriter.WrittenRecords![0];
            // Renderer succeeded in the fake → status verified, but the
            // missing-font diagnostics survive.
            Assert.Equal(MigrationStatus.VerifiedComplete, record.Status);
            Assert.Equal(2, record.MissingFontsCount);
            Assert.Equal("Lemon Cookie Bold | New York", record.MissingFontsList);

            // The preflight report still lists the file under each missing font.
            var report = engine.LatestFontPreflightReport;
            Assert.True(report.HasFindings);
            Assert.Contains(p, report.FilesByMissingFont["Lemon Cookie Bold"]);
            Assert.Contains(p, report.FilesByMissingFont["New York"]);

            // The log makes the override path obvious.
            Assert.Contains(capturedLog, l => l.Contains("Override") && l.Contains("Lemon Cookie Bold"));
            Assert.Contains(capturedLog, l => l.Contains("Font pre-flight summary"));
        }

        [Fact]
        public async Task Override_On_KeepsSourceFileEvenWithDeleteOption()
        {
            string sourceDir = Path.Combine(_testWorkspaceDir, "OverrideKeep");
            Directory.CreateDirectory(sourceDir);
            string p = Path.Combine(sourceDir, "x.pub");
            File.WriteAllText(p, "fake");

            var auditor = new FakeFontAuditor();
            auditor.MissingByPath[p] = new[] { "Missing One" };

            var options = new ConversionOptions
            {
                SourcePath = sourceDir,
                OverrideFontSkip = true,
                DeleteSourceOnSuccess = true,
            };
            var engine = CreateEngine(auditor);

            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            // Renderer ran (override let it through) and the fake produced a
            // valid PDF, but the source must NOT be deleted while missing-font
            // diagnostics are still attached.
            Assert.True(File.Exists(p));
            Assert.True(_manifestWriter.WrittenRecords![0].MissingFontsCount > 0);
        }

        [Fact]
        public async Task AutoResolve_On_Override_On_FailsResolverButStillRenders()
        {
            string sourceDir = Path.Combine(_testWorkspaceDir, "AutoOnOverrideOn");
            Directory.CreateDirectory(sourceDir);
            string p = Path.Combine(sourceDir, "x.pub");
            File.WriteAllText(p, "fake");

            var auditor = new FakeFontAuditor();
            auditor.MissingByPath[p] = new[] { "Lemon Cookie Bold" };
            var resolver = new FakeFontResolver(); // resolution will fail

            var options = new ConversionOptions
            {
                SourcePath = sourceDir,
                EnableAutoFontInstallation = true,
                OverrideFontSkip = true,
            };
            var engine = CreateEngine(auditor, resolver);

            await engine.ExecuteMigrationAsync(options, new FakeProgressReporter(), CancellationToken.None);

            Assert.Equal(1, resolver.CallCount);
            Assert.Equal(1, _renderer.RenderCount);

            var record = _manifestWriter.WrittenRecords![0];
            Assert.Equal(MigrationStatus.VerifiedComplete, record.Status);
            Assert.Equal("Lemon Cookie Bold", record.MissingFontsList);
        }

        private class FakeProgressReporter : IProgressReporter
        {
            public void Report(ProgressReport report) { }
        }

        private class CapturingProgressReporter : IProgressReporter
        {
            private readonly List<string> _sink;
            public CapturingProgressReporter(List<string> sink) { _sink = sink; }
            public void Report(ProgressReport report)
            {
                if (!string.IsNullOrEmpty(report.CurrentActionMessage)) _sink.Add(report.CurrentActionMessage);
            }
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

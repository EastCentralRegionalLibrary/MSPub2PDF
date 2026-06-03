using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    public class ConverterEngine
    {
        private readonly IFileInspector _inspector;
        private readonly IHashProvider _hashProvider;
        private readonly IManifestWriter _manifestWriter;
        private readonly IPublisherRenderer _renderer;
        private readonly Func<string, bool, IArchiveService> _archiveServiceFactory;

        public ConverterEngine(
            IFileInspector inspector,
            IHashProvider hashProvider,
            IManifestWriter manifestWriter,
            IPublisherRenderer renderer,
            Func<string, bool, IArchiveService> archiveServiceFactory)
        {
            _inspector = inspector;
            _hashProvider = hashProvider;
            _manifestWriter = manifestWriter;
            _renderer = renderer;
            _archiveServiceFactory = archiveServiceFactory;
        }

        public async Task ExecuteMigrationAsync(ConversionOptions options, IProgress<ProgressReport> progress, CancellationToken cancellationToken)
        {
            await ExecuteMigrationAsync(options, new ProgressReporterWrapper(progress), cancellationToken);
        }

        public async Task ExecuteMigrationAsync(ConversionOptions options, IProgressReporter reporter, CancellationToken cancellationToken)
        {
            List<FileRecord> indexedFiles = new List<FileRecord>();
            var report = new ProgressReport();
            string scratchRoot = Path.Combine(Path.GetTempPath(), "MSPub2PDF", Guid.NewGuid().ToString("N"));
            IArchiveService? archiveService = null;

            if (!string.IsNullOrEmpty(options.ArchivePath))
            {
                try
                {
                    archiveService = _archiveServiceFactory(options.ArchivePath, options.CompressArchive);
                    archiveService.Initialize();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to initialize backup directory structure: {ex.Message}", ex);
                }
            }

            try
            {
                // Phase 1: Discovery & Indexing
                reporter.Report(UpdateProgressState(report, "Scanning source folder for Publisher files...", null));
                if (!Directory.Exists(options.SourcePath)) throw new DirectoryNotFoundException("Source path selection is invalid.");

                if (!IsDirectoryWritable(options.SourcePath)) throw new UnauthorizedAccessException($"Source directory is not writable: {options.SourcePath}");
                if (!string.IsNullOrEmpty(options.ArchivePath) && !IsDirectoryWritable(options.ArchivePath)) throw new UnauthorizedAccessException($"Archive directory is not writable: {options.ArchivePath}");
                if (!string.IsNullOrEmpty(options.ManifestOutputPath) && !IsDirectoryWritable(options.ManifestOutputPath)) throw new UnauthorizedAccessException($"Manifest output directory is not writable: {options.ManifestOutputPath}");

                var files = FindPubFiles(options.SourcePath);

                if (options.SkipSourcePaths != null && options.SkipSourcePaths.Count > 0)
                {
                    int beforeCount = files.Count;
                    files = files.Where(f => !options.SkipSourcePaths.Contains(f)).ToList();
                    int skipped = beforeCount - files.Count;
                    if (skipped > 0)
                    {
                        reporter.Report(UpdateProgressState(report, $"Resuming — skipped {skipped} previously attempted file(s).", null));
                    }
                }

                report.TotalFiles = files.Count;

                if (report.TotalFiles == 0)
                {
                    reporter.Report(UpdateProgressState(report, "No .pub files found.", null));
                    return;
                }

                foreach (var file in files)
                {
                    string relativePathFile = Path.GetRelativePath(options.SourcePath, file);
                    string relative = Path.GetDirectoryName(relativePathFile) ?? "";
                    if (relative == ".") relative = "";

                    var info = new FileInfo(file);
                    indexedFiles.Add(new FileRecord
                    {
                        FileName = info.Name,
                        RelativePath = relative,
                        OriginalFullPath = file,
                        CreationTime = info.CreationTime,
                        LastWriteTime = info.LastWriteTime,
                        SourceSizeLength = info.Length,
                        LocalPubPath = Path.Combine(scratchRoot, relative.TrimStart('\\', '/'), info.Name),
                        LocalPdfPath = Path.Combine(scratchRoot, relative.TrimStart('\\', '/'), Path.ChangeExtension(info.Name, ".pdf")),
                        FinalPdfPath = Path.Combine(Path.GetDirectoryName(file)!, Path.ChangeExtension(info.Name, ".pdf"))
                    });
                }

                // Initialize renderer asynchronously (non-blocking on GUI thread)
                await _renderer.InitializeAsync(cancellationToken);

                int successSinceRecycle = 0;

                // Phase 2: Processing Loop
                foreach (var record in indexedFiles)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    if (options.EnableProcessRecycle && options.ProcessRecycleInterval > 0 && successSinceRecycle >= options.ProcessRecycleInterval)
                    {
                        _renderer.Recycle();
                        successSinceRecycle = 0;
                    }

                    // Step A: Static Triage
                    var safety = _inspector.InspectFile(record.OriginalFullPath);
                    record.HasMacros = safety.HasMacros;

                    if (safety.IsPasswordProtected || safety.IsCorruptedOrInvalid)
                    {
                        record.Status = MigrationStatus.FailedConversion;
                        record.Details = safety.Reason;

                        report.FailureCount++;
                        report.ProcessedFiles++;
                        record.ProcessedAtUtc = DateTime.UtcNow;
                        reporter.Report(UpdateProgressState(report, $"Skipped static triage: {record.FileName}", record));
                        continue;
                    }
                    record.SourceHash = _hashProvider.GetSha256Hash(record.OriginalFullPath);

                    // Step B: Ingress Staging
                    try
                    {
                        if (IsRemotePath(record.OriginalFullPath))
                        {
                            string localDir = Path.GetDirectoryName(record.LocalPubPath)!;
                            if (!Directory.Exists(localDir)) Directory.CreateDirectory(localDir);
                            File.Copy(record.OriginalFullPath, record.LocalPubPath, true);
                        }
                        else
                        {
                            record.LocalPubPath = record.OriginalFullPath;
                            string scratchDir = Path.GetDirectoryName(record.LocalPdfPath)!;
                            if (!Directory.Exists(scratchDir)) Directory.CreateDirectory(scratchDir);
                        }
                        record.Status = MigrationStatus.Staged;
                    }
                    catch (Exception ex)
                    {
                        record.Status = MigrationStatus.FailedIngress;
                        record.Details = $"Ingress Lock/IO Exception: {ex.Message}";

                        report.FailureCount++;
                        report.ProcessedFiles++;
                        record.ProcessedAtUtc = DateTime.UtcNow;
                        reporter.Report(UpdateProgressState(report, $"Ingress Failure: {record.FileName}", record));
                        continue;
                    }

                    // Per-document fallback strategy. State resets on each new
                    // document so a degradation applied here doesn't bleed into
                    // the next file.
                    //   - Export error → step Intent down one rung (Commercial
                    //     → Printing → Standard); record gets a _printres /
                    //     _standardres suffix on the output filename.
                    //   - Publisher crash → after the first two attempts,
                    //     turn DocStructureTags off; output gets _notags.
                    //   - Timeout → retry as-is (no parameter change).
                    const int maxFileAttempts = 3;
                    bool renderingSucceeded = false;
                    Exception? lastRenderingException = null;

                    RenderIntent currentIntent = RenderIntent.Commercial;
                    bool docStructureTags = true;
                    bool hadEngineCrash = false;
                    string baseLocalPdfPath = record.LocalPdfPath;
                    string baseFinalPdfPath = record.FinalPdfPath;

                    for (int attempt = 1; attempt <= maxFileAttempts; attempt++)
                    {
                        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);

                        string suffix = ComputeFallbackSuffix(currentIntent, docStructureTags);
                        string thisLocalPdfPath = ApplyPdfSuffix(baseLocalPdfPath, suffix);
                        string thisFinalPdfPath = ApplyPdfSuffix(baseFinalPdfPath, suffix);

                        string attemptPrefix = attempt > 1 ? $"[Retry {attempt}/{maxFileAttempts}] " : "";
                        string statusMsg = string.IsNullOrEmpty(suffix)
                            ? $"{attemptPrefix}Processing: {record.FileName}"
                            : $"{attemptPrefix}Processing {record.FileName} with degraded settings (intent={currentIntent}, tags={(docStructureTags ? "on" : "off")}) → {Path.GetFileName(thisLocalPdfPath)}";
                        reporter.Report(UpdateProgressState(report, statusMsg, record));

                        try
                        {
                            await _renderer.ExecuteRenderingJobAsync(
                                record,
                                record.LocalPubPath,
                                thisLocalPdfPath,
                                options.RunLinkCheck,
                                currentIntent,
                                docStructureTags,
                                options.FileTimeoutSeconds,
                                cancellationToken);

                            renderingSucceeded = true;
                            record.LocalPdfPath = thisLocalPdfPath;
                            record.FinalPdfPath = thisFinalPdfPath;
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (TimeoutException ex)
                        {
                            lastRenderingException = ex;
                            record.HadFailedAttempt = true;
                            record.Details = $"Attempt {attempt} timed out — retrying with the same settings.";
                            // No parameter change: timeouts retry as-is per spec.
                        }
                        catch (RenderEngineCrashException ex)
                        {
                            lastRenderingException = ex;
                            record.HadFailedAttempt = true;
                            hadEngineCrash = true;
                            record.Details = $"Attempt {attempt}: Publisher engine crashed during export.";
                        }
                        catch (RenderExportFailureException ex)
                        {
                            lastRenderingException = ex;
                            record.HadFailedAttempt = true;
                            record.Details = $"Attempt {attempt}: export failed — {ex.Message.Trim()}";
                            currentIntent = NextLowerIntent(currentIntent);
                        }
                        catch (Exception ex)
                        {
                            lastRenderingException = ex;
                            record.HadFailedAttempt = true;
                            record.Details = $"Attempt {attempt}: {ex.GetType().Name} — {ex.Message.Trim()}";
                            currentIntent = NextLowerIntent(currentIntent);
                        }

                        // After two attempts, if we've seen a crash, drop the
                        // accessibility tags for the next try.
                        if (hadEngineCrash && attempt >= 2 && docStructureTags)
                        {
                            docStructureTags = false;
                        }
                    }

                    // Evaluate execution resolution OUTSIDE the attempt loop to calculate the proper global health metric
                    if (renderingSucceeded)
                    {
                        // Note: batch success cleared only after egress validation below
                        record.Status = record.MissingAssetsCount > 0 ? MigrationStatus.VerifiedWithWarnings : MigrationStatus.VerifiedComplete;
                    }
                    else
                    {
                        // The file has completely exhausted all 3 local retries. Record a batch strike.
                        // If 3 completely different files do this back-to-back, this call trips the circuit breaker and stops the run.
                        _renderer.RecordBatchFailure();

                        record.Status = MigrationStatus.FailedConversion;
                        record.Details = lastRenderingException is TimeoutException
                            ? $"Execution Timeout: Process exceeded rendering thresholds across {maxFileAttempts} attempts."
                            : $"COM Rendering Exception: {lastRenderingException?.Message.Trim()} (Exhausted {maxFileAttempts} attempts).";

                        record.ProcessedAtUtc = DateTime.UtcNow;
                        if (CircuitBreakerTripped(options))
                        {
                            throw new CircuitBreakerTrippedException(_renderer.ConsecutiveFailures, CollectAttemptedSourcePaths(indexedFiles));
                        }
                    }

                    // Step C: Egress & Metadata Output Cloning
                    if (renderingSucceeded)
                    {
                        string? backupPath = null;
                        try
                        {
                            if (IsPlausiblePdf(record.LocalPdfPath))
                            {
                                if (File.Exists(record.FinalPdfPath))
                                {
                                    backupPath = record.FinalPdfPath + ".old";
                                    if (File.Exists(backupPath)) File.Delete(backupPath);
                                    File.Move(record.FinalPdfPath, backupPath);
                                }

                                if (record.LocalPdfPath != record.FinalPdfPath)
                                {
                                    File.Copy(record.LocalPdfPath, record.FinalPdfPath, true);
                                }

                                var finalPdfInfo = new FileInfo(record.FinalPdfPath)
                                {
                                    CreationTime = record.CreationTime,
                                    LastWriteTime = record.LastWriteTime
                                };

                                record.OutputHash = _hashProvider.GetSha256Hash(record.FinalPdfPath);
                                record.Details = record.MissingAssetsCount > 0
                                    ? $"Archived cleanly but missing {record.MissingAssetsCount} external dependencies."
                                    : "Converted cleanly to PDF/A.";

                                archiveService?.StageFile(record.OriginalFullPath, record.RelativePath, record.FileName);
                                _renderer.RecordBatchSuccess();

                                if (backupPath != null && File.Exists(backupPath)) File.Delete(backupPath);
                            }
                            else
                            {
                                record.Status = MigrationStatus.FailedEgress;
                                record.Details = $"PDF generation completed but scratch file was missing at {record.LocalPdfPath}.";

                                if (backupPath != null && File.Exists(backupPath)) File.Move(backupPath, record.FinalPdfPath, true);

                                _renderer.RecordBatchFailure();
                                record.ProcessedAtUtc = DateTime.UtcNow;
                                if (CircuitBreakerTripped(options)) throw new CircuitBreakerTrippedException(_renderer.ConsecutiveFailures, CollectAttemptedSourcePaths(indexedFiles));
                            }
                        }
                        // A circuit-breaker trip raised by the hollow-PDF branch above
                        // must propagate, not be re-handled here as an egress save
                        // failure (which would double-count the strike).
                        catch (Exception ex) when (ex is not CircuitBreakerTrippedException)
                        {
                            record.Status = MigrationStatus.FailedEgress;
                            record.Details = $"Egress Save Failure: {ex.Message}";

                            if (backupPath != null && File.Exists(backupPath))
                            {
                                try { File.Move(backupPath, record.FinalPdfPath, true); } catch { }
                            }

                            _renderer.RecordBatchFailure();
                            record.ProcessedAtUtc = DateTime.UtcNow;
                            if (CircuitBreakerTripped(options)) throw new CircuitBreakerTrippedException(_renderer.ConsecutiveFailures, CollectAttemptedSourcePaths(indexedFiles));
                        }
                    }

                    if (record.Status == MigrationStatus.VerifiedComplete)
                    {
                        report.SuccessCount++;
                        successSinceRecycle++;
                    }
                    else if (record.Status == MigrationStatus.VerifiedWithWarnings)
                    {
                        report.WarningCount++;
                        successSinceRecycle++;
                    }
                    else report.FailureCount++;

                    record.ProcessedAtUtc = DateTime.UtcNow;
                    report.ProcessedFiles++;
                    reporter.Report(UpdateProgressState(report, $"Finished: {record.FileName}", record));
                }

                if (archiveService != null)
                {
                    reporter.Report(UpdateProgressState(report, "Creating ZIP archive...", null));
                    archiveService.FinalizeArchive();
                }

                if (options.DeleteSourceOnSuccess)
                {
                    reporter.Report(UpdateProgressState(report, "Deleting successfully converted source files...", null));
                    foreach (var record in indexedFiles)
                    {
                        // Only delete originals that converted cleanly on the
                        // first try with no retries. Anything that had a
                        // failed attempt (including degraded successes) stays
                        // put so the user can review what changed.
                        if (record.Status == MigrationStatus.VerifiedComplete
                            && !record.HadFailedAttempt
                            && IsPlausiblePdf(record.FinalPdfPath))
                        {
                            try
                            {
                                if (record.OriginalFullPath != record.FinalPdfPath)
                                {
                                    File.Delete(record.OriginalFullPath);
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            finally
            {
                try
                {
                    string manifestDir = options.ManifestOutputPath ?? (archiveService != null ? options.ArchivePath : options.SourcePath);
                    _manifestWriter.WriteManifest(manifestDir, indexedFiles);
                }
                catch { }

                try { _renderer.Shutdown(); } catch { }

                try
                {
                    if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, true);
                }
                catch { }

                try { archiveService?.Dispose(); } catch { }
            }
        }

        private bool CircuitBreakerTripped(ConversionOptions options)
        {
            return options.EnableCircuitBreaker
                && options.MaxConsecutiveFailures > 0
                && _renderer.ConsecutiveFailures >= options.MaxConsecutiveFailures;
        }

        internal static RenderIntent NextLowerIntent(RenderIntent intent) => intent switch
        {
            RenderIntent.Commercial => RenderIntent.Printing,
            RenderIntent.Printing   => RenderIntent.Standard,
            _                       => RenderIntent.Standard
        };

        internal static string ComputeFallbackSuffix(RenderIntent intent, bool docStructureTags)
        {
            string suffix = "";
            switch (intent)
            {
                case RenderIntent.Printing: suffix += "_printres"; break;
                case RenderIntent.Standard: suffix += "_standardres"; break;
            }
            if (!docStructureTags) suffix += "_notags";
            return suffix;
        }

        internal static string ApplyPdfSuffix(string pdfPath, string suffix)
        {
            if (string.IsNullOrEmpty(suffix)) return pdfPath;
            string dir = Path.GetDirectoryName(pdfPath) ?? "";
            string name = Path.GetFileNameWithoutExtension(pdfPath);
            string ext = Path.GetExtension(pdfPath);
            return Path.Combine(dir, name + suffix + ext);
        }

        private static List<string> CollectAttemptedSourcePaths(List<FileRecord> indexed)
        {
            var attempted = new List<string>(indexed.Count);
            foreach (var r in indexed)
            {
                if (r.Status != MigrationStatus.Pending && !string.IsNullOrEmpty(r.OriginalFullPath))
                {
                    attempted.Add(r.OriginalFullPath);
                }
            }
            return attempted;
        }

        public static List<string> FindPubFiles(string root)
        {
            var results = new List<string>();
            if (!Directory.Exists(root)) return results;

            foreach (var file in Directory.GetFiles(root, "*.pub", SearchOption.AllDirectories))
            {
                if (Path.GetExtension(file).Equals(".pub", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(file);
                }
            }
            return results;
        }

        private ProgressReport UpdateProgressState(ProgressReport masterReport, string actionMessage, FileRecord? currentFile)
        {
            return new ProgressReport
            {
                TotalFiles = masterReport.TotalFiles,
                ProcessedFiles = masterReport.ProcessedFiles,
                SuccessCount = masterReport.SuccessCount,
                WarningCount = masterReport.WarningCount,
                FailureCount = masterReport.FailureCount,
                CurrentActionMessage = actionMessage,
                CurrentFile = currentFile
            };
        }

        public static bool IsDirectoryWritable(string dirPath)
        {
            try
            {
                if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
                string probeFile = Path.Combine(dirPath, Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probeFile, "probe");
                File.Delete(probeFile);
                return true;
            }
            catch { return false; }
        }

        public static bool IsRemotePath(string path)
        {
            // Detect UNC paths
            if (path.StartsWith("\\\\") || path.StartsWith("//")) return true;

            // Detect mapped network drives (Windows only)
            try
            {
                string? root = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root))
                {
                    var drive = new DriveInfo(root);
                    return drive.DriveType == DriveType.Network;
                }
            }
            catch { }

            return false;
        }

        public static bool IsPlausiblePdf(string path)
        {
            if (!File.Exists(path)) return false;
            var info = new FileInfo(path);
            if (info.Length < 10) return false;

            try
            {
                using var stream = File.OpenRead(path);
                byte[] buffer = new byte[5];
                int read = stream.Read(buffer, 0, 5);
                if (read < 5) return false;
                string header = System.Text.Encoding.ASCII.GetString(buffer);
                return header.Equals("%PDF-", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static string CsvEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";

            string escapedValue = value;
            // Neutralize formula-injection leads
            if (escapedValue.StartsWith("=") || escapedValue.StartsWith("+") || escapedValue.StartsWith("-") || escapedValue.StartsWith("@"))
            {
                escapedValue = "'" + escapedValue;
            }

            return "\"" + escapedValue.Replace("\"", "\"\"") + "\"";
        }

        private class ProgressReporterWrapper : IProgressReporter
        {
            private readonly IProgress<ProgressReport> _inner;
            public ProgressReporterWrapper(IProgress<ProgressReport> inner) => _inner = inner;
            public void Report(ProgressReport report) => _inner.Report(report);
        }
    }
}

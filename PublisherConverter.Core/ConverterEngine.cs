using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    public class ConverterEngine
    {
        public Task ExecuteMigrationAsync(ConversionOptions options, IProgress<ProgressReport> progress, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource();

            // The orchestrator thread manages the file batch queue, completely decoupled from COM handles
            var orchestratorThread = new Thread(() =>
            {
                var report = new ProgressReport();
                string scratchRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "_pubscratch");
                var indexedFiles = new List<FileRecord>();
                ArchiveService? archiveService = null;

                // Instantiate the lifecycle manager with a 3-strike consecutive file crash rule
                var lifecycleManager = new PublisherLifecycleManager(maxConsecutiveFailures: 3);

                if (!string.IsNullOrEmpty(options.ArchivePath))
                {
                    try
                    {
                        archiveService = new ArchiveService(options.ArchivePath);
                        archiveService.Initialize();
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(new InvalidOperationException($"Failed to initialize backup directory structure: {ex.Message}", ex));
                        return;
                    }
                }

                try
                {
                    // Phase 1: Discovery & Indexing
                    progress.Report(UpdateProgressState(report, "Scanning source folder for active Publisher files...", null));
                    if (!Directory.Exists(options.SourcePath)) throw new DirectoryNotFoundException("Source path selection is invalid.");

                    var files = Directory.GetFiles(options.SourcePath, "*.pub", SearchOption.AllDirectories);
                    report.TotalFiles = files.Length;

                    if (report.TotalFiles == 0)
                    {
                        progress.Report(UpdateProgressState(report, "Run complete. Zero .pub targets discovered.", null));
                        tcs.SetResult();
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
                            LocalPubPath = Path.Combine(scratchRoot, relative.TrimStart('\\'), info.Name),
                            LocalPdfPath = Path.Combine(scratchRoot, relative.TrimStart('\\'), Path.ChangeExtension(info.Name, ".pdf")),
                            FinalPdfPath = Path.Combine(Path.GetDirectoryName(file)!, Path.ChangeExtension(info.Name, ".pdf"))
                        });
                    }

                    // Bootstrap the isolated background worker apartment container
                    lifecycleManager.InitializeEngine();

                    // Phase 2: Processing Loop
                    foreach (var record in indexedFiles)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        // Step A: Static Triage (Inspect without mounting the COM engine)
                        var safety = PublisherInspector.InspectFile(record.OriginalFullPath);
                        if (safety.IsPasswordProtected || safety.IsCorruptedOrInvalid)
                        {
                            record.Status = MigrationStatus.FailedConversion;
                            record.Details = safety.Reason;

                            report.FailureCount++;
                            report.ProcessedFiles++;
                            progress.Report(UpdateProgressState(report, $"Skipped static triage: {record.FileName}", record));
                            continue;
                        }
                        record.SourceHash = GetSha256Hash(record.OriginalFullPath);

                        // Step B: Ingress Staging (Local Copying)
                        try
                        {
                            string localDir = Path.GetDirectoryName(record.LocalPubPath)!;
                            if (!Directory.Exists(localDir)) Directory.CreateDirectory(localDir);
                            File.Copy(record.OriginalFullPath, record.LocalPubPath, true);
                            record.Status = MigrationStatus.Staged;
                        }
                        catch (Exception ex)
                        {
                            record.Status = MigrationStatus.FailedIngress;
                            record.Details = $"Ingress Lock/IO Exception: {ex.Message}";

                            report.FailureCount++;
                            report.ProcessedFiles++;
                            progress.Report(UpdateProgressState(report, $"Ingress Failure: {record.FileName}", record));
                            continue;
                        }

                        const int maxFileAttempts = 3;
                        bool renderingSucceeded = false;

                        // Intra-File Local Retry Loop
                        for (int attempt = 1; attempt <= maxFileAttempts; attempt++)
                        {
                            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);

                            string attemptPrefix = attempt > 1 ? $"[Retry {attempt}/{maxFileAttempts}] " : "";
                            progress.Report(UpdateProgressState(report, $"{attemptPrefix}Processing: {record.FileName}", record));

                            try
                            {
                                // Dispatch the file variables down to the isolated worker apartment
                                lifecycleManager.ExecuteRenderingJob(record, record.LocalPubPath, record.LocalPdfPath, options.RunLinkCheck, options.FileTimeoutSeconds, cancellationToken);

                                renderingSucceeded = true;
                                record.Status = record.MissingAssetsCount > 0 ? MigrationStatus.VerifiedWithWarnings : MigrationStatus.VerifiedComplete;
                                break; // Rendering succeeded; break out of the local retry attempts loop
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                // REFACTOR: Intercept the sequential circuit breaker exception.
                                // Rethrow instantly to escape the retry loop and queue to trigger an orderly cleanup.
                                if (ex.Message.Contains("circuit breaker tripped"))
                                {
                                    throw;
                                }

                                if (attempt < maxFileAttempts)
                                {
                                    record.Details = $"Attempt {attempt} thrown out ({ex.GetType().Name}). Accessing local retry block...";
                                    continue;
                                }

                                // All local retry options exhausted for this file
                                record.Status = MigrationStatus.FailedConversion;
                                record.Details = ex is TimeoutException
                                    ? $"Execution Timeout: Process exceeded rendering thresholds across {maxFileAttempts} attempts."
                                    : $"COM Rendering Exception: {ex.Message.Trim()} (Exhausted {maxFileAttempts} attempts).";
                            }
                        }

                        // Step C: Egress & Metadata Cloning
                        if (renderingSucceeded)
                        {
                            try
                            {
                                if (File.Exists(record.LocalPdfPath))
                                {
                                    File.Copy(record.LocalPdfPath, record.FinalPdfPath, true);

                                    var finalPdfInfo = new FileInfo(record.FinalPdfPath)
                                    {
                                        CreationTime = record.CreationTime,
                                        LastWriteTime = record.LastWriteTime
                                    };

                                    record.OutputHash = GetSha256Hash(record.FinalPdfPath);
                                    record.Details = record.MissingAssetsCount > 0
                                        ? $"Archived cleanly but missing {record.MissingAssetsCount} external dependencies."
                                        : "Converted cleanly to PDF/A.";

                                    archiveService?.StageFile(record.OriginalFullPath, record.RelativePath, record.FileName);
                                }
                                else
                                {
                                    record.Status = MigrationStatus.FailedEgress;
                                    record.Details = "PDF generation completed but scratch file was missing.";
                                }
                            }
                            catch (Exception ex)
                            {
                                record.Status = MigrationStatus.FailedEgress;
                                record.Details = $"Egress Save Failure: {ex.Message}";
                            }
                        }

                        // Update metrics tracking
                        if (record.Status == MigrationStatus.VerifiedComplete) report.SuccessCount++;
                        else if (record.Status == MigrationStatus.VerifiedWithWarnings) report.WarningCount++;
                        else report.FailureCount++;

                        report.ProcessedFiles++;
                        progress.Report(UpdateProgressState(report, $"Finished: {record.FileName}", record));
                    }

                    if (archiveService != null)
                    {
                        progress.Report(UpdateProgressState(report, "Compressing historical archive repository to ZIP container...", null));
                        archiveService.FinalizeArchive();
                    }

                    if (options.DeleteSourceOnSuccess)
                    {
                        progress.Report(UpdateProgressState(report, "Cleaning up source network footprints...", null));
                        foreach (var record in indexedFiles)
                        {
                            if ((record.Status == MigrationStatus.VerifiedComplete || record.Status == MigrationStatus.VerifiedWithWarnings) && File.Exists(record.FinalPdfPath))
                            {
                                try { File.Delete(record.OriginalFullPath); } catch { }
                            }
                        }
                    }

                    ExportAuditTrailCsv(options.SourcePath, indexedFiles);
                    tcs.SetResult();
                }
                catch (OperationCanceledException ex)
                {
                    tcs.SetCanceled(ex.CancellationToken);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
                finally
                {
                    // Gracefully shutdown the engine and clear local workspace structures
                    lifecycleManager.ShutdownEngine();
                    if (Directory.Exists(scratchRoot))
                    {
                        try { Directory.Delete(scratchRoot, true); } catch { }
                    }
                }
            });

            orchestratorThread.IsBackground = true;
            orchestratorThread.Start();

            return tcs.Task;
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

        public static string GetSha256Hash(string filePath)
        {
            if (!File.Exists(filePath)) return "Missing";
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(filePath);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private void ExportAuditTrailCsv(string directory, List<FileRecord> records)
        {
            string csvPath = Path.Combine(directory, $"MigrationManifest_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            using var writer = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8);
            writer.WriteLine("Timestamp,FileName,RelativeFolder,Status,SourceHash,OutputHash,MissingLinkCount,Details,LinkManifest");

            foreach (var r in records)
            {
                writer.WriteLine($"\"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\",\"{r.FileName}\",\"{r.RelativePath}\",\"{r.Status}\",\"{r.SourceHash}\",\"{r.OutputHash}\",{r.MissingAssetsCount},\"{r.Details.Replace("\"", "\"\"")}\",\"{r.MissingAssetsList.Replace("\"", "\"\"")}\"");
            }
        }
    }
}
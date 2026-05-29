using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Office.Interop.Publisher;

namespace PublisherConverter.Core
{
    public class ConverterEngine
    {
        private Application? _pubApp;
        private int _loopCounter = 0;
        private readonly object _comLock = new object();

        // Type.Missing placeholder for optional parameter blocks in legacy COM
        private static readonly object Missing = Type.Missing;

        public async Task ExecuteMigrationAsync(ConversionOptions options, IProgress<ProgressReport> progress, CancellationToken cancellationToken)
        {
            // FIX (CS1998): Satisfies async state machine context immediately
            await Task.Yield();

            var report = new ProgressReport();
            string scratchRoot = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "_pubscratch");
            var indexedFiles = new List<FileRecord>();
            ArchiveService? archiveService = null;
            if (!string.IsNullOrEmpty(options.ArchivePath))
            {
                try
                {
                    archiveService = new ArchiveService(options.ArchivePath);
                    archiveService.Initialize();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Failed to instantiate archive layout frameworks: {ex.Message}", ex);
                }
            }

            try
            {
                // Phase 1: Discovery & Indexing
                progress.Report(new ProgressReport { CurrentActionMessage = "Scanning source folder for active Publisher files..." });
                if (!Directory.Exists(options.SourcePath)) throw new DirectoryNotFoundException("Source path selection is invalid.");

                var files = Directory.GetFiles(options.SourcePath, "*.pub", SearchOption.AllDirectories);
                report.TotalFiles = files.Length;

                if (report.TotalFiles == 0)
                {
                    progress.Report(new ProgressReport { CurrentActionMessage = "Run complete. Zero .pub targets discovered." });
                    return;
                }

                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    string relative = Path.GetDirectoryName(file)?[options.SourcePath.Length..] ?? "";

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

                // Phase 2: Start Headless Engine
                InitializePublisherEngine();

                // Phase 3: Processing Loop
                foreach (var record in indexedFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    report.CurrentFile = record;
                    report.CurrentActionMessage = $"Processing: {record.FileName}";
                    progress.Report(report);

                    // Step A: Static Triage
                    var safety = PublisherInspector.InspectFile(record.OriginalFullPath);
                    if (safety.IsPasswordProtected || safety.IsCorruptedOrInvalid)
                    {
                        record.Status = MigrationStatus.FailedConversion;
                        record.Details = safety.Reason;
                        report.FailureCount++;
                        report.ProcessedFiles++;
                        continue;
                    }
                    record.SourceHash = GetSha256Hash(record.OriginalFullPath);

                    // Step B: Ingress (Local Staging)
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
                        continue;
                    }

                    // Step C: Link Audit & Conversion Rendering
                    Document? doc = null;
                    bool processingTimedOut = false;

                    using (var timeoutCts = new CancellationTokenSource())
                    {
                        // Spawn a background timer task that monitors this file's rendering progress
                        var watchdogTask = Task.Delay(TimeSpan.FromSeconds(options.FileTimeoutSeconds), timeoutCts.Token)
                            .ContinueWith(t =>
                            {
                                // If the delay task finishes naturally without being cancelled, a hang occurred!
                                if (!t.IsCanceled)
                                {
                                    processingTimedOut = true;
                                    KillPublisherProcesses(); // Forcefully kill mspub.exe to unblock the main thread
                                }
                            });
                        try
                        {
                            lock (_comLock)
                            {
                                // FIX (CS1739): Cleaned up type signature arguments matching Publisher's API rules
                                doc = _pubApp!.Open(Filename: record.LocalPubPath, ReadOnly: true);

                                if (options.RunLinkCheck)
                                {
                                    AuditDocumentLinks(doc, record);
                                }

                                archiveService?.StageFile(record.OriginalFullPath, record.RelativePath, record.FileName);

                                // Force strict PDF/A alignment matching commercial layout rules
                                doc.ExportAsFixedFormat(
                                    Format: PbFixedFormatType.pbFixedFormatTypePDF,
                                    Filename: record.LocalPdfPath,
                                    Intent: PbFixedFormatIntent.pbIntentCommercial,
                                    IncludeDocumentProperties: true,
                                    DocStructureTags: true,
                                    BitmapMissingFonts: true,
                                    UseISO19005_1: true // Enforces PDF/A Archival standard
                                );
                            }

                            timeoutCts.Cancel();
                            record.Status = record.MissingAssetsCount > 0 ? MigrationStatus.VerifiedWithWarnings : MigrationStatus.VerifiedComplete;
                        }
                        catch (Exception ex)
                        {
                            timeoutCts.Cancel(); // Ensure timer task resources release safely

                            if (processingTimedOut)
                            {
                                record.Status = MigrationStatus.FailedConversion;
                                record.Details = $"Execution Timeout: Exceeded allocated limits of {options.FileTimeoutSeconds}s. Underlying engine process terminated.";
                                _pubApp = null; // Clear instance variable to trigger fresh initialization on next loop pass
                            }
                            else
                            {
                                record.Status = MigrationStatus.FailedConversion;
                                record.Details = $"COM Rendering Exception: {ex.Message.Trim()}";
                            }
                        }
                        finally
                        {
                            lock (_comLock)
                            {
                                if (doc != null)
                                {
                                    doc.Close();
                                    System.Runtime.InteropServices.Marshal.ReleaseComObject(doc);
                                }
                            }
                        }
                    }

                    // Step D: Egress & Metadata Cloning
                    if (record.Status == MigrationStatus.VerifiedComplete || record.Status == MigrationStatus.VerifiedWithWarnings)
                    {
                        try
                        {
                            if (File.Exists(record.LocalPdfPath))
                            {
                                File.Copy(record.LocalPdfPath, record.FinalPdfPath, true);

                                // Reset target dates to match the original layout records exactly
                                var finalPdfInfo = new FileInfo(record.FinalPdfPath)
                                {
                                    CreationTime = record.CreationTime,
                                    LastWriteTime = record.LastWriteTime
                                };

                                record.OutputHash = GetSha256Hash(record.FinalPdfPath);
                                record.Details = record.MissingAssetsCount > 0
                                    ? $"Archived cleanly but missing {record.MissingAssetsCount} external dependencies."
                                    : "Converted cleanly to PDF/A.";

                                if (record.MissingAssetsCount > 0) report.WarningCount++; else report.SuccessCount++;
                            }
                            else
                            {
                                record.Status = MigrationStatus.FailedEgress;
                                record.Details = "PDF generation completed but scratch file was missing.";
                                report.FailureCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            record.Status = MigrationStatus.FailedEgress;
                            record.Details = $"Egress Save Failure: {ex.Message}";
                            report.FailureCount++;
                        }
                    }
                    else
                    {
                        report.FailureCount++;
                    }

                    // Step E: Housekeeping & Process Recycling Check
                    report.ProcessedFiles++;
                    _loopCounter++;
                    if (_loopCounter >= options.ProcessRecycleInterval)
                    {
                        RecyclePublisherProcess(progress);
                    }
                }
                if (archiveService != null)
                {
                    progress.Report(new ProgressReport { CurrentActionMessage = "Compressing historical archive repository to ZIP container..." });
                    archiveService.FinalizeArchive();
                }

                // Phase 4: Optional Purges
                if (options.DeleteSourceOnSuccess)
                {
                    progress.Report(new ProgressReport { CurrentActionMessage = "Cleaning up source network footprints..." });
                    foreach (var record in indexedFiles)
                    {
                        if ((record.Status == MigrationStatus.VerifiedComplete || record.Status == MigrationStatus.VerifiedWithWarnings) && File.Exists(record.FinalPdfPath))
                        {
                            File.Delete(record.OriginalFullPath);
                        }
                    }
                }

                ExportAuditTrailCsv(options.SourcePath, indexedFiles);
            }
            finally
            {
                // Guarantee full machine engine isolation teardown on pipeline exit
                ShutdownPublisherEngine();
                if (Directory.Exists(scratchRoot)) Directory.Delete(scratchRoot, true);
            }
        }

        private void InitializePublisherEngine()
        {
            lock (_comLock)
            {
                _pubApp = new Application();
                _pubApp.AutomationSecurity = Microsoft.Office.Core.MsoAutomationSecurity.msoAutomationSecurityForceDisable;
                _loopCounter = 0;
            }
        }

        private void RecyclePublisherProcess(IProgress<ProgressReport> progress)
        {
            progress.Report(new ProgressReport { CurrentActionMessage = "Recycling underlying Office automation framework..." });
            ShutdownPublisherEngine();
            InitializePublisherEngine();
        }

        private void ShutdownPublisherEngine()
        {
            lock (_comLock)
            {
                if (_pubApp != null)
                {
                    try { _pubApp.Quit(); } catch { }
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(_pubApp);
                    _pubApp = null;
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        private void KillPublisherProcesses()
        {
            foreach (var process in Process.GetProcessesByName("mspub"))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000); // Allow 5 seconds for OS cleanup routines to bind
                }
                catch { }
            }
        }
        private void AuditDocumentLinks(Document doc, FileRecord record)
        {
            var brokenAssets = new List<string>();

            foreach (Page page in doc.Pages)
            {
                foreach (Shape shape in page.Shapes)
                {
                    try
                    {
                        // FIX (CS1061): Modified casing to lowercase 'n' (.Filename) to resolve property resolution errors
                        if (shape.PictureFormat != null && shape.PictureFormat.IsLinked == Microsoft.Office.Core.MsoTriState.msoTrue)
                        {
                            string fileRef = shape.PictureFormat.Filename;
                            if (!File.Exists(fileRef)) brokenAssets.Add($"[Page {page.PageNumber}] Image Link: {fileRef}");
                        }
                    }
                    catch { }

                    try
                    {
                        if (shape.LinkFormat != null && !string.IsNullOrEmpty(shape.LinkFormat.SourceFullName))
                        {
                            string OleRef = shape.LinkFormat.SourceFullName;
                            if (!File.Exists(OleRef)) brokenAssets.Add($"[Page {page.PageNumber}] OLE Data Ref: {OleRef}");
                        }
                    }
                    catch { }
                }
            }

            if (brokenAssets.Count > 0)
            {
                record.MissingAssetsCount = brokenAssets.Count;
                record.MissingAssetsList = string.Join(" | ", brokenAssets);
            }
        }

        public static string GetSha256Hash(string filePath)
        {
            if (!File.Exists(filePath)) return "Missing";
            using var sha = SHA256.Create();
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
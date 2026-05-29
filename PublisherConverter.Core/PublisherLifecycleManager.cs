using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Office.Interop.Publisher;

namespace PublisherConverter.Core
{
    public class PublisherLifecycleManager
    {
        private int _consecutiveFailureCount = 0;
        private readonly int _maxConsecutiveFailures;
        private readonly object _stateLock = new object();

        private WorkerApartment? _activeWorker;

        public PublisherLifecycleManager(int maxConsecutiveFailures = 3)
        {
            _maxConsecutiveFailures = maxConsecutiveFailures;
        }

        public void InitializeEngine()
        {
            lock (_stateLock)
            {
                VerifyEngineAvailability();
                _activeWorker = new WorkerApartment();
                _activeWorker.Start();
            }
        }

        public void ShutdownEngine()
        {
            lock (_stateLock)
            {
                _activeWorker?.Shutdown();
                _activeWorker = null;
            }
        }

        // FIX: Dedicated batch-level tracking hooks to separate single attempts from global file results
        public void RecordBatchSuccess()
        {
            lock (_stateLock)
            {
                _consecutiveFailureCount = 0;
            }
        }

        public void RecordBatchFailure()
        {
            lock (_stateLock)
            {
                _consecutiveFailureCount++;
                if (_consecutiveFailureCount >= _maxConsecutiveFailures)
                {
                    throw new InvalidOperationException(
                        $"Automation engine circuit breaker tripped. {_consecutiveFailureCount} separate files " +
                        $"have failed completely in a row. Aborting execution batch to protect system resources.");
                }
            }
        }

        public void ExecuteRenderingJob(FileRecord record, string sourcePubPath, string targetPdfPath, bool runLinkCheck, int timeoutSeconds, CancellationToken cancellationToken)
        {
            lock (_stateLock)
            {
                if (_activeWorker == null)
                {
                    _activeWorker = new WorkerApartment();
                    _activeWorker.Start();
                }

                try
                {
                    _activeWorker.ExecuteJob(record, sourcePubPath, targetPdfPath, runLinkCheck, timeoutSeconds, cancellationToken);
                }
                catch (Exception)
                {
                    // FIX: Clean up the broken apartment wrapper and rethrow immediately.
                    // We no longer call RecordFailure() here, preventing local file retries from inflating the global batch score.
                    _activeWorker?.ForceTeardown();
                    _activeWorker = null;

                    _activeWorker = new WorkerApartment();
                    _activeWorker.Start();

                    throw;
                }
            }
        }

        private void VerifyEngineAvailability()
        {
            Application? app = null;
            try
            {
                app = new Application();
                app.AutomationSecurity = Microsoft.Office.Core.MsoAutomationSecurity.msoAutomationSecurityForceDisable;
                app.Quit();
            }
            catch (Exception ex)
            {
                _consecutiveFailureCount++;
                throw new InvalidOperationException($"Inbound COM automation factory validation failed: {ex.Message}", ex);
            }
            finally
            {
                if (app != null) { Marshal.ReleaseComObject(app); app = null; }
            }
        }

        private class WorkerApartment
        {
            private readonly AutoResetEvent _jobReadyEvent = new AutoResetEvent(false);
            private readonly AutoResetEvent _jobFinishedEvent = new AutoResetEvent(false);
            private readonly AutoResetEvent _processExitedEvent = new AutoResetEvent(false);

            private string? _sourcePubPath;
            private string? _targetPdfPath;
            private bool _runLinkCheck;
            private FileRecord? _currentRecord;

            private Application? _sharedPubApp;
            private System.Diagnostics.Process? _trackedProcess;
            private Exception? _workerException;
            private Thread? _thread;

            private bool _isTerminated = false;
            private bool _isTeardownIntentional = false;

            public void Start()
            {
                _thread = new Thread(WorkerLoop);
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.IsBackground = true;
                _thread.Start();
            }

            public void Shutdown()
            {
                _isTerminated = true;
                _jobReadyEvent.Set();
                ForceTeardown();
            }

            public void ExecuteJob(FileRecord record, string sourcePubPath, string targetPdfPath, bool runLinkCheck, int timeoutSeconds, CancellationToken cancellationToken)
            {
                _sourcePubPath = sourcePubPath;
                _targetPdfPath = targetPdfPath;
                _runLinkCheck = runLinkCheck;
                _currentRecord = record;
                _workerException = null;
                _isTeardownIntentional = false;

                _jobFinishedEvent.Reset();
                _processExitedEvent.Reset();

                _jobReadyEvent.Set();

                int waitSignalIndex = WaitHandle.WaitAny(new WaitHandle[] {
                    _jobFinishedEvent,
                    _processExitedEvent,
                    cancellationToken.WaitHandle
                }, TimeSpan.FromSeconds(timeoutSeconds));

                if (waitSignalIndex == WaitHandle.WaitTimeout)
                {
                    throw new TimeoutException($"Headless rendering execution window threshold of {timeoutSeconds}s exceeded.");
                }
                if (waitSignalIndex == 2)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                if (waitSignalIndex == 1)
                {
                    throw new InvalidOperationException("The underlying Microsoft Publisher background process crashed unexpectedly.");
                }
                if (_workerException != null)
                {
                    throw _workerException;
                }
            }

            private void WorkerLoop()
            {
                while (!_isTerminated)
                {
                    _jobReadyEvent.WaitOne();
                    if (_isTerminated) break;

                    try
                    {
                        if (_sharedPubApp == null)
                        {
                            _sharedPubApp = new Application();
                            _sharedPubApp.AutomationSecurity = Microsoft.Office.Core.MsoAutomationSecurity.msoAutomationSecurityForceDisable;

                            int pid = GetCurrentSessionNewestPublisherPid();
                            if (pid != 0)
                            {
                                _trackedProcess = System.Diagnostics.Process.GetProcessById(pid);
                                _trackedProcess.EnableRaisingEvents = true;
                                _trackedProcess.Exited += (s, e) =>
                                {
                                    if (!_isTeardownIntentional) _processExitedEvent.Set();
                                };
                            }
                        }

                        if (_sourcePubPath != null && _targetPdfPath != null)
                        {
                            Document? doc = null;
                            try
                            {
                                doc = _sharedPubApp.Open(Filename: _sourcePubPath, ReadOnly: true);

                                if (_runLinkCheck && _currentRecord != null)
                                {
                                    AuditDocumentLinks(doc, _currentRecord);
                                }

                                doc.ExportAsFixedFormat(
                                    Format: PbFixedFormatType.pbFixedFormatTypePDF,
                                    Filename: _targetPdfPath,
                                    Intent: PbFixedFormatIntent.pbIntentCommercial,
                                    IncludeDocumentProperties: true,
                                    DocStructureTags: true,
                                    BitmapMissingFonts: true,
                                    UseISO19005_1: true
                                );
                            }
                            finally
                            {
                                if (doc != null)
                                {
                                    try { doc.Close(); Marshal.ReleaseComObject(doc); } catch { }
                                    doc = null;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _workerException = ex;
                    }
                    finally
                    {
                        _jobFinishedEvent.Set();
                    }
                }

                if (_sharedPubApp != null)
                {
                    try { _sharedPubApp.Quit(); Marshal.ReleaseComObject(_sharedPubApp); } catch { }
                    _sharedPubApp = null;
                }
            }

            public void ForceTeardown()
            {
                _isTerminated = true;
                _isTeardownIntentional = true;

                if (_trackedProcess != null)
                {
                    try { _trackedProcess.EnableRaisingEvents = false; } catch { }
                    _trackedProcess = null;
                }

                _jobReadyEvent.Set();

                int currentSessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;

                foreach (var process in System.Diagnostics.Process.GetProcessesByName("mspub"))
                {
                    try { if (process.SessionId == currentSessionId) { process.Kill(); process.WaitForExit(2000); } } catch { }
                }

                foreach (var process in System.Diagnostics.Process.GetProcessesByName("WerFault"))
                {
                    try { if (process.SessionId == currentSessionId) { process.Kill(); } } catch { }
                }
            }

            private int GetCurrentSessionNewestPublisherPid()
            {
                try
                {
                    int currentSessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                    System.Diagnostics.Process? newestProc = null;
                    DateTime newestTime = DateTime.MinValue;

                    foreach (var proc in System.Diagnostics.Process.GetProcessesByName("mspub"))
                    {
                        try
                        {
                            if (proc.SessionId == currentSessionId)
                            {
                                if (newestProc == null || proc.StartTime > newestTime)
                                {
                                    newestProc = proc;
                                    newestTime = proc.StartTime;
                                }
                            }
                        }
                        catch { }
                    }
                    return newestProc?.Id ?? 0;
                }
                catch { return 0; }
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
        }
    }
}
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

        // The active isolated worker apartment instance
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
                    // Pass execution down into the cleanly isolated thread apartment container
                    _activeWorker.ExecuteJob(record, sourcePubPath, targetPdfPath, runLinkCheck, timeoutSeconds, cancellationToken);

                    // On a verified success, reset the consecutive failure counter
                    _consecutiveFailureCount = 0;
                }
                catch (Exception)
                {
                    // An exception occurred (timeout, crash, or COM channel fault). 
                    // Sever all ties to the dead apartment and destroy its process footprints immediately.
                    _activeWorker?.ForceTeardown();
                    _activeWorker = null; // Discard completely to let GC collect its synchronization events

                    RecordFailure(); // Increment circuit breaker thresholds

                    // Pre-instantiate a clean replacement apartment container for the next queue file/retry attempt
                    _activeWorker = new WorkerApartment();
                    _activeWorker.Start();

                    throw; // Rethrow to let ConverterEngine execute its file-level retry strategies
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

        private void RecordFailure()
        {
            _consecutiveFailureCount++;
            if (_consecutiveFailureCount >= _maxConsecutiveFailures)
            {
                throw new InvalidOperationException(
                    $"Automation engine circuit breaker tripped. Headless Publisher process failed " +
                    $"to initialize {_consecutiveFailureCount} times consecutively. Aborting execution batch to protect system resources.");
            }
        }

        /// <summary>
        /// Fully encapsulated, self-contained STA Thread Apartment environment.
        /// </summary>
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
                _isTerminated = true; // Signals loop termination to this thread container specifically
                _isTeardownIntentional = true;

                if (_trackedProcess != null)
                {
                    try { _trackedProcess.EnableRaisingEvents = false; } catch { }
                    _trackedProcess = null;
                }

                _jobReadyEvent.Set(); // Wakes thread up out of wait state to process closure if sleeping

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
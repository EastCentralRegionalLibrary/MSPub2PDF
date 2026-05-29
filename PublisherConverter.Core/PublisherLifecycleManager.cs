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

        // Cross-apartment thread synchronization event wires
        private readonly AutoResetEvent _jobReadyEvent = new AutoResetEvent(false);
        private readonly AutoResetEvent _jobFinishedEvent = new AutoResetEvent(false);
        private readonly AutoResetEvent _processExitedEvent = new AutoResetEvent(false); // NEW: Event signal for process crashes

        // Context parameters exchanged between master engine loops and worker apartment
        private string? _sourcePubPath;
        private string? _targetPdfPath;
        private bool _runLinkCheck;
        private FileRecord? _currentRecord;

        private Application? _sharedPubApp;
        private System.Diagnostics.Process? _trackedProcess; // NEW: Explicitly track the Process lifecycle object
        private Exception? _workerException;
        private bool _isWorkerTerminated = false;
        private bool _isTeardownIntentional = false; // NEW: Flag to distinguish a crash from a clean cleanup
        private Thread? _workerThread;

        public PublisherLifecycleManager(int maxConsecutiveFailures = 3)
        {
            _maxConsecutiveFailures = maxConsecutiveFailures;
        }

        public void InitializeEngine()
        {
            lock (_stateLock)
            {
                VerifyEngineAvailability();
                _workerThread = StartNewActiveWorkerApartment();
            }
        }

        public void ShutdownEngine()
        {
            lock (_stateLock)
            {
                _isWorkerTerminated = true;
                _jobReadyEvent.Set();
                ForceTeardown();
                _sharedPubApp = null;
            }
        }

        /// <summary>
        /// Submits a job and uses low-level Win32 kernel wait handles to listen for events reactively.
        /// </summary>
        public void ExecuteRenderingJob(FileRecord record, string sourcePubPath, string targetPdfPath, bool runLinkCheck, int timeoutSeconds, CancellationToken cancellationToken)
        {
            lock (_stateLock)
            {
                _sourcePubPath = sourcePubPath;
                _targetPdfPath = targetPdfPath;
                _runLinkCheck = runLinkCheck;
                _currentRecord = record;
                _workerException = null;
                _isTeardownIntentional = false;

                // Clear out any stale signaling frames before beginning execution pass
                _jobFinishedEvent.Reset();
                _processExitedEvent.Reset();

                _jobReadyEvent.Set();

                // EVENT-DRIVEN KERNEL WAIT BLOCK
                // The OS puts this thread to sleep at 0% CPU, waking it instantly if any handle triggers or the timeout expires.
                int waitSignalIndex = WaitHandle.WaitAny(new WaitHandle[] {
                    _jobFinishedEvent,
                    _processExitedEvent,
                    cancellationToken.WaitHandle
                }, TimeSpan.FromSeconds(timeoutSeconds));

                if (waitSignalIndex == WaitHandle.WaitTimeout)
                {
                    // Case 1: The process hung or exceeded its timeout limit
                    CleanResetDeadWorker();
                    RecordFailure();
                    throw new TimeoutException($"Headless rendering execution window threshold of {timeoutSeconds}s exceeded.");
                }

                if (waitSignalIndex == 2)
                {
                    // Case 2: Manual User Abort button clicked
                    _isWorkerTerminated = true;
                    _jobReadyEvent.Set();
                    ForceTeardown();
                    throw new OperationCanceledException(cancellationToken);
                }

                if (waitSignalIndex == 1)
                {
                    // Case 3: ProcessExitedEvent fired (The background application crashed instantly)
                    CleanResetDeadWorker();
                    RecordFailure();
                    throw new InvalidOperationException("The underlying Microsoft Publisher background process crashed unexpectedly.");
                }

                // Case 4: Job completed successfully. Evaluate any inner managed exceptions.
                if (_workerException != null)
                {
                    CleanResetDeadWorker();
                    RecordFailure();
                    throw _workerException;
                }

                // Reset the system-level sequential fault circuit breaker
                _consecutiveFailureCount = 0;
            }
        }

        private Thread StartNewActiveWorkerApartment()
        {
            _isWorkerTerminated = false;
            var thread = new Thread(DedicatedWorkerApartmentLoop);
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            return thread;
        }

        private void DedicatedWorkerApartmentLoop()
        {
            while (!_isWorkerTerminated)
            {
                _jobReadyEvent.WaitOne();
                if (_isWorkerTerminated) break;

                try
                {
                    if (_sharedPubApp == null)
                    {
                        _sharedPubApp = new Application();
                        _sharedPubApp.AutomationSecurity = Microsoft.Office.Core.MsoAutomationSecurity.msoAutomationSecurityForceDisable;

                        // EVENT SUBSCRIPTION: Fetch the native process container and subscribe to its exit signal
                        int pid = GetCurrentSessionNewestPublisherPid();
                        if (pid != 0)
                        {
                            _trackedProcess = System.Diagnostics.Process.GetProcessById(pid);
                            _trackedProcess.EnableRaisingEvents = true;
                            _trackedProcess.Exited += OnPublisherProcessExited;
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

        /// <summary>
        /// Reactor callback executed automatically by the OS threadpool the exact millisecond mspub exits or crashes.
        /// </summary>
        private void OnPublisherProcessExited(object? sender, EventArgs e)
        {
            // If we are intentionally tearing down the engine, ignore this signal callback
            if (_isTeardownIntentional) return;

            // Signal the master loop thread that the process crashed
            _processExitedEvent.Set();
        }

        private void CleanResetDeadWorker()
        {
            ForceTeardown();

            _isWorkerTerminated = true;
            _jobReadyEvent.Set();
            _sharedPubApp = null;

            _workerThread = StartNewActiveWorkerApartment();
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

        public void ForceTeardown()
        {
            _isTeardownIntentional = true; // Set flag to inhibit event loop race callbacks

            if (_trackedProcess != null)
            {
                try { _trackedProcess.Exited -= OnPublisherProcessExited; } catch { }
                _trackedProcess = null;
            }

            int currentSessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;

            foreach (var process in System.Diagnostics.Process.GetProcessesByName("mspub"))
            {
                try { if (process.SessionId == currentSessionId) { process.Kill(); process.WaitForExit(2000); } } catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException) { }
            }

            foreach (var process in System.Diagnostics.Process.GetProcessesByName("WerFault"))
            {
                try { if (process.SessionId == currentSessionId) { process.Kill(); } } catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException) { }
            }
        }
    }
}
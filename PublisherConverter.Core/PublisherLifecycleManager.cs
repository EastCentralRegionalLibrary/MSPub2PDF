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

        // Context parameters exchanged between master engine loops and worker apartment
        private string? _sourcePubPath;
        private string? _targetPdfPath;
        private bool _runLinkCheck;
        private FileRecord? _currentRecord;

        private Application? _sharedPubApp;
        private Exception? _workerException;
        private bool _isWorkerTerminated = false;
        private Thread? _workerThread;

        public PublisherLifecycleManager(int maxConsecutiveFailures = 3)
        {
            _maxConsecutiveFailures = maxConsecutiveFailures;
        }

        /// <summary>
        /// Bootstraps the thread apartment structures and warms up initial background automation processes.
        /// </summary>
        public void InitializeEngine()
        {
            lock (_stateLock)
            {
                VerifyEngineAvailability();
                _isWorkerTerminated = false;
                _workerThread = StartNewActiveWorkerApartment();
            }
        }

        /// <summary>
        /// Gracefully deconstructs and purges active native background environment modules.
        /// </summary>
        public void ShutdownEngine()
        {
            lock (_stateLock)
            {
                _isWorkerTerminated = true;
                _jobReadyEvent.Set(); // Releases loop out of blocking state if active
                ForceTeardown();
                _sharedPubApp = null;
            }
        }

        /// <summary>
        /// Submits staged source file parameters down to the isolated worker container window block for execution.
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

                // Signal proxy worker thread apartment container to pick up execution properties
                _jobReadyEvent.Set();

                // Low-level kernel signal window wait block
                int waitSignalIndex = WaitHandle.WaitAny(new WaitHandle[] { _jobFinishedEvent, cancellationToken.WaitHandle }, TimeSpan.FromSeconds(timeoutSeconds));

                if (waitSignalIndex == WaitHandle.WaitTimeout)
                {
                    ForceTeardown();
                    RecordFailure();

                    _isWorkerTerminated = true;
                    _jobReadyEvent.Set();
                    _sharedPubApp = null;

                    // Discard the frozen thread context and spin up a replacement proxy container
                    _workerThread = StartNewActiveWorkerApartment();

                    throw new TimeoutException($"Headless rendering execution window threshold of {timeoutSeconds}s exceeded.");
                }

                if (waitSignalIndex == 1) // CancellationToken triggered by Abort button
                {
                    _isWorkerTerminated = true;
                    _jobReadyEvent.Set();
                    ForceTeardown();
                    throw new OperationCanceledException(cancellationToken);
                }

                // Evaluate processing worker thread results
                if (_workerException != null)
                {
                    ForceTeardown();
                    _sharedPubApp = null;
                    RecordFailure();

                    // Rethrow exception context back up to Master Engine loop to handle local file retries
                    throw _workerException;
                }

                // Operation successfully completed: Clear global sequential fault metrics
                _consecutiveFailureCount = 0;
            }
        }

        private Thread StartNewActiveWorkerApartment()
        {
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
                    }

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
                catch (Exception ex)
                {
                    _workerException = ex;
                }
                finally
                {
                    _jobFinishedEvent.Set(); // Notify Master thread wait loop context
                }
            }

            if (_sharedPubApp != null)
            {
                try { _sharedPubApp.Quit(); Marshal.ReleaseComObject(_sharedPubApp); } catch { }
                _sharedPubApp = null;
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
            int currentSessionId = Process.GetCurrentProcess().SessionId;

            foreach (var process in Process.GetProcessesByName("mspub"))
            {
                try { if (process.SessionId == currentSessionId) { process.Kill(); process.WaitForExit(2000); } } catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException) { }
            }

            foreach (var process in Process.GetProcessesByName("WerFault"))
            {
                try { if (process.SessionId == currentSessionId) { process.Kill(); } } catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException) { }
            }
        }
    }
}
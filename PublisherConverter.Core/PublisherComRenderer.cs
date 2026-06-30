using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Office.Interop.Publisher;

namespace PublisherConverter.Core
{
    /// <summary>
    /// COM-based document renderer driving Microsoft Publisher to export PDFs.
    /// Used by the production worker process. Tests should use a stub renderer
    /// (see PublisherConverter.TestWorker.StubDocumentRenderer).
    ///
    /// A background watchdog polls the mspub.exe process so we can detect a
    /// Publisher crash quickly. A synchronous COM RPC into a dead Publisher
    /// won't unblock on its own, so without the watchdog the worker would
    /// hang until the client-side request timeout fired (~60 s by default).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class PublisherComRenderer : IDocumentRenderer
    {
        private const string PublisherProcessName = "mspub";
        private const int WatchdogPollMs = 200;

        private Application? _pubApp;
        private Process? _publisherProcess;
        private CancellationTokenSource? _watchdogCts;
        private Thread? _watchdogThread;

        // Worker-tier kill-on-close job holding the DCOM-activated mspub.exe.
        // mspub is not a child of this worker process (it is activated under
        // DcomLaunch), so killing the worker's process tree never reaches it.
        // Assigning its PID to a kill-on-close job means a worker death — clean
        // or otherwise — reaps mspub via the OS. IntPtr.Zero when unavailable.
        private IntPtr _mspubJob = IntPtr.Zero;

        public event EventHandler? EngineCrashed;

        public void Initialize()
        {
            // Snapshot pre-existing mspub.exe PIDs so we can identify the new
            // instance after `new Application()`. If Publisher was already
            // running, COM may attach to it and we won't see a new PID — in
            // that case we skip the watchdog rather than guess wrong.
            HashSet<int> preExistingPids = SnapshotPublisherPids();

            _pubApp = new Application();
            _pubApp.AutomationSecurity = Microsoft.Office.Core.MsoAutomationSecurity.msoAutomationSecurityForceDisable;

            _publisherProcess = FindOwnedPublisherProcess(preExistingPids);
            if (_publisherProcess != null)
            {
                StartWatchdog(_publisherProcess);
                TryAssignPublisherToKillOnCloseJob(_publisherProcess.Id);
            }
        }

        // Assigns the activated mspub.exe to a kill-on-close job so a worker
        // death reaps Publisher. Best-effort: a failure leaves the watchdog +
        // graceful Quit() as the cleanup path and never blocks initialization.
        private void TryAssignPublisherToKillOnCloseJob(int publisherPid)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                _mspubJob = WorkerJobObject.CreateKillOnCloseJob();
                if (_mspubJob == IntPtr.Zero) return;
                if (!WorkerJobObject.AssignProcessById(_mspubJob, publisherPid))
                {
                    WorkerJobObject.CloseJob(_mspubJob);
                    _mspubJob = IntPtr.Zero;
                }
            }
            catch
            {
                _mspubJob = IntPtr.Zero;
            }
        }

        public RenderResult Render(RenderJob job)
        {
            if (_pubApp == null) throw new InvalidOperationException("Renderer not initialized. Call Initialize() first.");

            if (_publisherProcess != null && _publisherProcess.HasExited)
            {
                throw new InvalidOperationException("Publisher process is no longer running.");
            }

            Document? doc = null;
            try
            {
                doc = _pubApp.Open(Filename: job.SourcePubPath, ReadOnly: true);

                var result = new RenderResult();
                if (job.RunLinkCheck) AuditDocumentLinks(doc, result);

                doc.ExportAsFixedFormat(
                    Format: PbFixedFormatType.pbFixedFormatTypePDF,
                    Filename: job.TargetPdfPath,
                    Intent: MapIntent(job.Intent),
                    IncludeDocumentProperties: true,
                    DocStructureTags: job.DocStructureTags,
                    BitmapMissingFonts: true,
                    UseISO19005_1: true);

                return result;
            }
            finally
            {
                if (doc != null)
                {
                    try { doc.Close(); } catch { }
                    try { Marshal.ReleaseComObject(doc); } catch { }
                }
            }
        }

        public void Dispose()
        {
            try { _watchdogCts?.Cancel(); } catch { }
            try { _watchdogThread?.Join(TimeSpan.FromSeconds(1)); } catch { }
            try { _watchdogCts?.Dispose(); } catch { }
            _watchdogCts = null;
            _watchdogThread = null;

            try { _publisherProcess?.Dispose(); } catch { }
            _publisherProcess = null;

            if (_pubApp != null)
            {
                try { _pubApp.Quit(); } catch { }
                try { Marshal.ReleaseComObject(_pubApp); } catch { }
                _pubApp = null;
            }

            // Close the kill-on-close job last. Graceful Quit() above normally
            // exits mspub on its own, leaving the job empty so this is a no-op.
            // If Quit() failed or hung, closing the job terminates the lingering
            // mspub.exe — the backstop that guarantees no orphan survives.
            if (_mspubJob != IntPtr.Zero)
            {
                try { WorkerJobObject.CloseJob(_mspubJob); } catch { }
                _mspubJob = IntPtr.Zero;
            }
        }

        private void StartWatchdog(Process publisherProcess)
        {
            _watchdogCts = new CancellationTokenSource();
            var token = _watchdogCts.Token;
            _watchdogThread = new Thread(() => WatchdogLoop(publisherProcess, token))
            {
                IsBackground = true,
                Name = "PublisherCrashWatchdog"
            };
            _watchdogThread.Start();
        }

        private void WatchdogLoop(Process publisherProcess, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    bool exited;
                    try { exited = publisherProcess.HasExited; }
                    catch { exited = true; } // process handle invalidated → treat as crash

                    if (exited)
                    {
                        EngineCrashed?.Invoke(this, EventArgs.Empty);
                        return;
                    }

                    Thread.Sleep(WatchdogPollMs);
                }
            }
            catch
            {
                // Defensive — the watchdog must not propagate exceptions
                // into the worker's main thread.
            }
        }

        private static HashSet<int> SnapshotPublisherPids()
        {
            try
            {
                return new HashSet<int>(Process.GetProcessesByName(PublisherProcessName).Select(p => p.Id));
            }
            catch
            {
                return new HashSet<int>();
            }
        }

        /// <summary>
        /// Pure PID-diff selection: the first PID in <paramref name="current"/>
        /// that was not present in <paramref name="preExisting"/> is the
        /// instance we just activated. Extracted from the Process enumeration so
        /// the "which PID did we own" decision is unit-testable without Publisher
        /// (a null result means COM attached to a pre-existing instance).
        /// </summary>
        internal static int? SelectOwnedPid(IReadOnlySet<int> preExisting, IReadOnlyCollection<int> current)
            => current.FirstOrDefault(pid => !preExisting.Contains(pid)) is var p && p != 0 ? p : (int?)null;

        private static Process? FindOwnedPublisherProcess(HashSet<int> preExistingPids)
        {
            try
            {
                var current = Process.GetProcessesByName(PublisherProcessName);
                int? ownedPid = SelectOwnedPid(preExistingPids, current.Select(p => p.Id).ToList());
                return ownedPid.HasValue ? current.FirstOrDefault(p => p.Id == ownedPid.Value) : null;
            }
            catch
            {
                return null;
            }
        }

        private static PbFixedFormatIntent MapIntent(RenderIntent intent) => intent switch
        {
            RenderIntent.Commercial => PbFixedFormatIntent.pbIntentCommercial,
            RenderIntent.Printing   => PbFixedFormatIntent.pbIntentPrinting,
            RenderIntent.Standard   => PbFixedFormatIntent.pbIntentStandard,
            _                       => PbFixedFormatIntent.pbIntentCommercial
        };

        private static void AuditDocumentLinks(Document doc, RenderResult result)
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
                            string oleRef = shape.LinkFormat.SourceFullName;
                            if (!File.Exists(oleRef)) brokenAssets.Add($"[Page {page.PageNumber}] OLE Data Ref: {oleRef}");
                        }
                    }
                    catch { }
                }
            }
            if (brokenAssets.Count > 0)
            {
                result.MissingAssetsCount = brokenAssets.Count;
                result.MissingAssetsList = string.Join(" | ", brokenAssets);
            }
        }
    }
}

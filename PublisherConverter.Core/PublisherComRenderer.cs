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
                    Intent: PbFixedFormatIntent.pbIntentCommercial,
                    IncludeDocumentProperties: true,
                    DocStructureTags: true,
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

        private static Process? FindOwnedPublisherProcess(HashSet<int> preExistingPids)
        {
            try
            {
                return Process.GetProcessesByName(PublisherProcessName)
                    .FirstOrDefault(p => !preExistingPids.Contains(p.Id));
            }
            catch
            {
                return null;
            }
        }

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

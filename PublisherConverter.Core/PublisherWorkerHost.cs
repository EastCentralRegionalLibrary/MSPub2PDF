using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Office.Interop.Publisher;

namespace PublisherConverter.Core
{
    public class PublisherWorkerHost
    {
        private readonly string _pipeName;
        private Application? _pubApp;

        public PublisherWorkerHost(string pipeName)
        {
            _pipeName = pipeName;
        }

        public void Run()
        {
            // Worker host runs synchronously on the STA thread to maintain thread affinity for COM.
            using var transport = new NamedPipeWorkerTransport(_pipeName, isServer: true);
            try
            {
                transport.Connect();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Worker failed to connect to pipe: {ex.Message}");
                return;
            }

            try
            {
                InitializePublisher();

                while (true)
                {
                    var request = transport.ReceiveRequestSync();
                    if (request == null) break;

                    if (request.Command == "shutdown")
                    {
                        transport.SendResponseSync(new WorkerResponse { Success = true });
                        break;
                    }

                    if (request.Command == "render" && request.RenderJob != null)
                    {
                        var response = ExecuteRender(request.RenderJob);
                        transport.SendResponseSync(response);
                    }
                    else if (request.Command == "health")
                    {
                        transport.SendResponseSync(new WorkerResponse { Success = true });
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    transport.SendResponseSync(new WorkerResponse { Success = false, ErrorMessage = ex.Message });
                }
                catch { }
            }
            finally
            {
                CleanupPublisher();
            }
        }

        private void InitializePublisher()
        {
            _pubApp = new Application();
            _pubApp.AutomationSecurity = Microsoft.Office.Core.MsoAutomationSecurity.msoAutomationSecurityForceDisable;
        }

        private void CleanupPublisher()
        {
            if (_pubApp != null)
            {
                try { _pubApp.Quit(); } catch { }
                try { Marshal.ReleaseComObject(_pubApp); } catch { }
                _pubApp = null;
            }
        }

        private WorkerResponse ExecuteRender(RenderJob job)
        {
            if (_pubApp == null) return new WorkerResponse { Success = false, ErrorMessage = "Publisher not initialized." };

            Document? doc = null;
            try
            {
                doc = _pubApp.Open(Filename: job.SourcePubPath, ReadOnly: true);

                var result = new RenderResult();
                if (job.RunLinkCheck)
                {
                    AuditDocumentLinks(doc, result);
                }

                doc.ExportAsFixedFormat(
                    Format: PbFixedFormatType.pbFixedFormatTypePDF,
                    Filename: job.TargetPdfPath,
                    Intent: PbFixedFormatIntent.pbIntentCommercial,
                    IncludeDocumentProperties: true,
                    DocStructureTags: true,
                    BitmapMissingFonts: true,
                    UseISO19005_1: true
                );

                return new WorkerResponse { Success = true, RenderResult = result };
            }
            catch (Exception ex)
            {
                return new WorkerResponse { Success = false, ErrorMessage = ex.Message };
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

        private void AuditDocumentLinks(Document doc, RenderResult result)
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
                result.MissingAssetsCount = brokenAssets.Count;
                result.MissingAssetsList = string.Join(" | ", brokenAssets);
            }
        }
    }
}

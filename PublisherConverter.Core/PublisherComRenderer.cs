using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Office.Interop.Publisher;

namespace PublisherConverter.Core
{
    /// <summary>
    /// COM-based document renderer driving Microsoft Publisher to export PDFs.
    /// Used by the production worker process. Tests should use a stub renderer
    /// (see PublisherConverter.TestWorker.StubDocumentRenderer).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class PublisherComRenderer : IDocumentRenderer
    {
        private Application? _pubApp;

        public void Initialize()
        {
            _pubApp = new Application();
            _pubApp.AutomationSecurity = Microsoft.Office.Core.MsoAutomationSecurity.msoAutomationSecurityForceDisable;
        }

        public RenderResult Render(RenderJob job)
        {
            if (_pubApp == null) throw new InvalidOperationException("Renderer not initialized. Call Initialize() first.");

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
            if (_pubApp != null)
            {
                try { _pubApp.Quit(); } catch { }
                try { Marshal.ReleaseComObject(_pubApp); } catch { }
                _pubApp = null;
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

using System;
using System.Collections.Generic;

namespace PublisherConverter.Core
{
    public class WorkerRequest
    {
        public string ProtocolVersion { get; set; } = "1.0";
        public string Command { get; set; } = string.Empty;
        public RenderJob? RenderJob { get; set; }
    }

    public class WorkerResponse
    {
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public RenderResult? RenderResult { get; set; }
    }

    public class RenderJob
    {
        public string SourcePubPath { get; set; } = string.Empty;
        public string TargetPdfPath { get; set; } = string.Empty;
        public bool RunLinkCheck { get; set; }

        /// <summary>
        /// Resolution intent for the PDF export. Maps to
        /// PbFixedFormatIntent in the COM renderer. Defaults to Commercial
        /// (highest quality) for backwards compatibility with existing requests.
        /// </summary>
        public RenderIntent Intent { get; set; } = RenderIntent.Commercial;

        /// <summary>
        /// Whether to emit accessibility/structure tags into the PDF. The
        /// orchestrator turns this off as a last-resort retry when Publisher
        /// is crashing on this particular document.
        /// </summary>
        public bool DocStructureTags { get; set; } = true;
    }

    public class RenderResult
    {
        public int MissingAssetsCount { get; set; }
        public string MissingAssetsList { get; set; } = "None";
    }

    /// <summary>
    /// Quality / resolution rung passed to Publisher's ExportAsFixedFormat.
    /// Mirrors PbFixedFormatIntent (commercial &gt; printing &gt; standard) but
    /// stays independent of the COM interop assembly so the test worker can
    /// honour the same protocol without taking the Interop reference.
    /// </summary>
    public enum RenderIntent
    {
        Commercial = 0,
        Printing = 1,
        Standard = 2
    }

    /// <summary>
    /// Raised by the renderer when the worker reports a logical export error
    /// (e.g. Publisher's ExportAsFixedFormat returned a failure) while the
    /// worker process itself is still alive. The orchestrator's per-document
    /// fallback ladder uses this to step the render intent down a rung.
    /// </summary>
    public class RenderExportFailureException : InvalidOperationException
    {
        public RenderExportFailureException(string message) : base(message) { }
        public RenderExportFailureException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Raised by the renderer when the worker's underlying engine (Publisher)
    /// crashes mid-export, or when the worker process itself dies and the
    /// IPC pipe goes away. The orchestrator uses repeated crashes as the
    /// signal to try turning off DocStructureTags.
    /// </summary>
    public class RenderEngineCrashException : InvalidOperationException
    {
        public RenderEngineCrashException(string message) : base(message) { }
        public RenderEngineCrashException(string message, Exception inner) : base(message, inner) { }
    }
}

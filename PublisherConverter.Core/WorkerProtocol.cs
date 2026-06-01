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
    }

    public class RenderResult
    {
        public int MissingAssetsCount { get; set; }
        public string MissingAssetsList { get; set; } = "None";
    }
}

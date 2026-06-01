using System;
using System.IO;
using System.Threading;
using PublisherConverter.Core;

namespace PublisherConverter.TestWorker
{
    /// <summary>
    /// Default test renderer. Writes a minimal PDF-shaped file at the target
    /// path so the orchestrator's IsPlausiblePdf check passes.
    /// </summary>
    public class StubDocumentRenderer : IDocumentRenderer
    {
        public void Initialize() { }

        public RenderResult Render(RenderJob job)
        {
            if (string.IsNullOrEmpty(job.TargetPdfPath))
            {
                throw new ArgumentException("Target PDF path is empty.");
            }

            string? dir = Path.GetDirectoryName(job.TargetPdfPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(
                job.TargetPdfPath,
                "%PDF-1.4\n%Stub PDF written by PublisherConverter.TestWorker\n");

            return new RenderResult();
        }

        public void Dispose() { }
    }

    /// <summary>Renderer that throws during Initialize, simulating a fatal startup error.</summary>
    public class FailingInitRenderer : IDocumentRenderer
    {
        public void Initialize() => throw new InvalidOperationException("Simulated init failure");
        public RenderResult Render(RenderJob job) => throw new InvalidOperationException("Not initialized");
        public void Dispose() { }
    }

    /// <summary>Renderer that always throws during Render, simulating a per-job error.</summary>
    public class FailingRenderRenderer : IDocumentRenderer
    {
        public void Initialize() { }
        public RenderResult Render(RenderJob job) => throw new InvalidOperationException("Simulated render failure");
        public void Dispose() { }
    }

    /// <summary>Renderer that blocks indefinitely during Render, used to exercise client-side timeouts.</summary>
    public class HangingRenderer : IDocumentRenderer
    {
        public void Initialize() { }
        public RenderResult Render(RenderJob job)
        {
            Thread.Sleep(Timeout.Infinite);
            return new RenderResult();
        }
        public void Dispose() { }
    }

    /// <summary>Renderer that terminates the worker process during Render, simulating a crash mid-job.</summary>
    public class CrashOnRenderRenderer : IDocumentRenderer
    {
        public void Initialize() { }
        public RenderResult Render(RenderJob job)
        {
            // Hard exit — the worker dies without sending a response.
            Environment.Exit(99);
            return new RenderResult();
        }
        public void Dispose() { }
    }
}

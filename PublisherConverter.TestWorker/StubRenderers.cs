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
#pragma warning disable CS0067 // EngineCrashed never raised by stub renderers; only the live COM renderer fires it.
        public event EventHandler? EngineCrashed;
#pragma warning restore CS0067

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
#pragma warning disable CS0067
        public event EventHandler? EngineCrashed;
#pragma warning restore CS0067
        public void Initialize() => throw new InvalidOperationException("Simulated init failure");
        public RenderResult Render(RenderJob job) => throw new InvalidOperationException("Not initialized");
        public void Dispose() { }
    }

    /// <summary>Renderer that always throws during Render, simulating a per-job error.</summary>
    public class FailingRenderRenderer : IDocumentRenderer
    {
#pragma warning disable CS0067
        public event EventHandler? EngineCrashed;
#pragma warning restore CS0067
        public void Initialize() { }
        public RenderResult Render(RenderJob job) => throw new InvalidOperationException("Simulated render failure");
        public void Dispose() { }
    }

    /// <summary>Renderer that blocks indefinitely during Render, used to exercise client-side timeouts.</summary>
    public class HangingRenderer : IDocumentRenderer
    {
#pragma warning disable CS0067
        public event EventHandler? EngineCrashed;
#pragma warning restore CS0067
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
#pragma warning disable CS0067
        public event EventHandler? EngineCrashed;
#pragma warning restore CS0067
        public void Initialize() { }
        public RenderResult Render(RenderJob job)
        {
            // Hard exit — the worker dies without sending a response.
            Environment.Exit(99);
            return new RenderResult();
        }
        public void Dispose() { }
    }

    /// <summary>
    /// Renderer that simulates the rendering engine (e.g. mspub.exe) dying
    /// asynchronously while a Render call is in flight. Raises EngineCrashed
    /// from a background thread and then blocks forever — modelling a COM RPC
    /// that's stuck on a dead Publisher. The worker host should detect the
    /// crash via the event, dispose the transport, and exit, so the client
    /// observes the failure long before its request timeout fires.
    /// </summary>
    public class EngineCrashRenderer : IDocumentRenderer
    {
        public event EventHandler? EngineCrashed;

        public void Initialize() { }

        public RenderResult Render(RenderJob job)
        {
            var trigger = new Thread(() =>
            {
                Thread.Sleep(100);
                EngineCrashed?.Invoke(this, EventArgs.Empty);
            })
            {
                IsBackground = true,
                Name = "EngineCrashTrigger"
            };
            trigger.Start();

            // Simulate the stuck COM RPC. The worker host's crash handler
            // calls Environment.Exit, so this will never actually return.
            Thread.Sleep(Timeout.Infinite);
            return new RenderResult();
        }

        public void Dispose() { }
    }
}

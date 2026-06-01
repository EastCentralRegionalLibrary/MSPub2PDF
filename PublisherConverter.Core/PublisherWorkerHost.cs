using System;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Hosts a document renderer behind a named-pipe request loop. The host
    /// itself is rendering-engine agnostic — callers inject an IDocumentRenderer
    /// (PublisherComRenderer in production, stub renderer in tests). This keeps
    /// the process lifecycle behavior independently testable from COM/Publisher.
    /// </summary>
    public class PublisherWorkerHost
    {
        private readonly string _pipeName;
        private readonly IDocumentRenderer _renderer;

        public PublisherWorkerHost(string pipeName, IDocumentRenderer renderer)
        {
            if (string.IsNullOrEmpty(pipeName)) throw new ArgumentException("Pipe name must be provided.", nameof(pipeName));
            _pipeName = pipeName;
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        }

        public void Run()
        {
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

            bool rendererInitialized = false;
            try
            {
                try
                {
                    _renderer.Initialize();
                    rendererInitialized = true;
                }
                catch (Exception ex)
                {
                    TrySendResponse(transport, new WorkerResponse { Success = false, ErrorMessage = $"Renderer init failed: {ex.Message}" });
                    return;
                }

                while (true)
                {
                    var request = transport.ReceiveRequestSync();
                    if (request == null) break;

                    switch (request.Command)
                    {
                        case "shutdown":
                            TrySendResponse(transport, new WorkerResponse { Success = true });
                            return;

                        case "health":
                            TrySendResponse(transport, new WorkerResponse { Success = true });
                            break;

                        case "render":
                            TrySendResponse(transport, ExecuteRender(request.RenderJob));
                            break;

                        default:
                            TrySendResponse(transport, new WorkerResponse { Success = false, ErrorMessage = $"Unknown command: {request.Command}" });
                            break;
                    }
                }
            }
            finally
            {
                if (rendererInitialized)
                {
                    try { _renderer.Dispose(); } catch { }
                }
            }
        }

        private WorkerResponse ExecuteRender(RenderJob? job)
        {
            if (job == null) return new WorkerResponse { Success = false, ErrorMessage = "Missing render job in request." };

            try
            {
                var result = _renderer.Render(job);
                return new WorkerResponse { Success = true, RenderResult = result };
            }
            catch (Exception ex)
            {
                return new WorkerResponse { Success = false, ErrorMessage = ex.Message };
            }
        }

        private static void TrySendResponse(NamedPipeWorkerTransport transport, WorkerResponse response)
        {
            try { transport.SendResponseSync(response); } catch { }
        }
    }
}

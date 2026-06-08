using System;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FontWorker
{
    /// <summary>
    /// Transport seam for the elevated font worker IPC channel. Hides all wire
    /// details (named pipe, in-memory loopback for tests, …) behind a
    /// request/response contract so neither the client nor the host depends on
    /// a concrete transport. The same interface serves both ends; a transport
    /// is created in either client or server role.
    /// </summary>
    public interface IFontWorkerTransport : IDisposable
    {
        // ---- Client side (async, cancellable, timeout-driven) ----

        Task ConnectAsync(CancellationToken cancellationToken, int? timeoutMs = null);
        Task SendRequestAsync(FontWorkerRequest request, CancellationToken cancellationToken);
        Task<FontWorkerResponse> ReceiveResponseAsync(CancellationToken cancellationToken);

        // ---- Server side (the worker host's blocking request loop) ----

        /// <summary>Binds the server endpoint and waits for the client to connect.</summary>
        void Connect();

        /// <summary>Blocks until a request arrives, or returns null when the client closes the pipe.</summary>
        FontWorkerRequest? ReceiveRequestSync();

        void SendResponseSync(FontWorkerResponse response);
    }
}

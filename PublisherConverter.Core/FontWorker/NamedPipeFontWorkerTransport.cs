using System;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FontWorker
{
    /// <summary>
    /// Named-pipe implementation of <see cref="IFontWorkerTransport"/>. Shares
    /// the length-prefixed framing in <see cref="PipeMessageFraming"/> with the
    /// render worker transport; this type only owns connection setup and
    /// (de)serialization of the font-worker envelopes.
    /// </summary>
    public sealed class NamedPipeFontWorkerTransport : IFontWorkerTransport
    {
        private const int DefaultServerWaitTimeoutMs = 30000;
        private const int DefaultClientConnectionTimeoutMs = 10000;
        private const int ConnectionRetryIntervalMs = 100;

        private readonly string _pipeName;
        private readonly bool _isServer;
        private PipeStream? _stream;

        public NamedPipeFontWorkerTransport(string pipeName, bool isServer = false)
        {
            _pipeName = pipeName ?? throw new ArgumentNullException(nameof(pipeName));
            _isServer = isServer;
        }

        public void Connect()
        {
            ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        public async Task ConnectAsync(CancellationToken cancellationToken, int? timeoutMs = null)
        {
            if (_isServer)
            {
                var serverStream = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(timeoutMs ?? DefaultServerWaitTimeoutMs);
                    await serverStream.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);
                    _stream = serverStream;
                }
                catch
                {
                    serverStream.Dispose();
                    throw;
                }
            }
            else
            {
                var clientStream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(timeoutMs ?? DefaultClientConnectionTimeoutMs);

                    // Retry until the worker's server endpoint is ready or our budget runs out.
                    while (true)
                    {
                        try
                        {
                            await clientStream.ConnectAsync(ConnectionRetryIntervalMs, cts.Token).ConfigureAwait(false);
                            _stream = clientStream;
                            return;
                        }
                        catch (TimeoutException) when (!cts.IsCancellationRequested)
                        {
                            await Task.Delay(ConnectionRetryIntervalMs, cts.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch
                {
                    clientStream.Dispose();
                    throw;
                }
            }
        }

        public async Task SendRequestAsync(FontWorkerRequest request, CancellationToken cancellationToken)
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, FontWorkerProtocol.JsonOptions);
            await PipeMessageFraming.WriteFramedAsync(_stream, payload, cancellationToken).ConfigureAwait(false);
        }

        public async Task<FontWorkerResponse> ReceiveResponseAsync(CancellationToken cancellationToken)
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            byte[]? payload = await PipeMessageFraming.ReadFramedAsync(_stream, cancellationToken).ConfigureAwait(false);
            if (payload == null) throw new InvalidOperationException("Worker closed the pipe before sending a response.");
            return JsonSerializer.Deserialize<FontWorkerResponse>(payload, FontWorkerProtocol.JsonOptions)
                   ?? throw new InvalidOperationException("Received null response from font worker.");
        }

        public FontWorkerRequest? ReceiveRequestSync()
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            byte[]? payload = PipeMessageFraming.ReadFramed(_stream);
            return payload == null ? null : JsonSerializer.Deserialize<FontWorkerRequest>(payload, FontWorkerProtocol.JsonOptions);
        }

        public void SendResponseSync(FontWorkerResponse response)
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(response, FontWorkerProtocol.JsonOptions);
            PipeMessageFraming.WriteFramed(_stream, payload);
        }

        public void Dispose()
        {
            _stream?.Dispose();
        }
    }
}

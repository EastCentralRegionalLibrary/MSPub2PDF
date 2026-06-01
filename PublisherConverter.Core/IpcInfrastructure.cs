using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Length-prefixed JSON message transport over a named pipe.
    ///
    /// Wire format: a single message is [4-byte little-endian length][UTF-8 JSON payload].
    /// We can't use JsonSerializer.(Des)erializeAsync directly on a PipeStream because
    /// the deserializer keeps reading until its buffer fills past a threshold OR the
    /// stream returns EOF — neither happens on a long-lived duplex pipe, so it blocks
    /// indefinitely even after a complete JSON value has arrived. Framing each message
    /// with its byte length lets us read exactly the right number of bytes.
    /// </summary>
    public class NamedPipeWorkerTransport : IWorkerTransport
    {
        private const int DefaultServerWaitTimeoutMs = 30000;
        private const int DefaultClientConnectionTimeoutMs = 10000;
        private const int ConnectionRetryIntervalMs = 100;
        private const int MaxMessageBytes = 16 * 1024 * 1024; // 16 MiB safety cap

        private readonly string _pipeName;
        private readonly bool _isServer;
        private PipeStream? _stream;

        public NamedPipeWorkerTransport(string pipeName, bool isServer = false)
        {
            _pipeName = pipeName;
            _isServer = isServer;
        }

        public void Connect()
        {
            // Sync wrapper, used by the worker process for its single up-front bind.
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

        public async Task SendRequestAsync(WorkerRequest request, CancellationToken cancellationToken)
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            await WriteFramedAsync(_stream, JsonSerializer.SerializeToUtf8Bytes(request), cancellationToken).ConfigureAwait(false);
        }

        public async Task SendResponseAsync(WorkerResponse response, CancellationToken cancellationToken)
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            await WriteFramedAsync(_stream, JsonSerializer.SerializeToUtf8Bytes(response), cancellationToken).ConfigureAwait(false);
        }

        public async Task<WorkerRequest?> ReceiveRequestAsync(CancellationToken cancellationToken)
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            byte[]? payload = await ReadFramedAsync(_stream, cancellationToken).ConfigureAwait(false);
            return payload == null ? null : JsonSerializer.Deserialize<WorkerRequest>(payload);
        }

        public async Task<WorkerResponse> ReceiveResponseAsync(CancellationToken cancellationToken)
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            byte[]? payload = await ReadFramedAsync(_stream, cancellationToken).ConfigureAwait(false);
            if (payload == null) throw new InvalidOperationException("Worker closed the pipe before sending a response.");
            return JsonSerializer.Deserialize<WorkerResponse>(payload)
                   ?? throw new InvalidOperationException("Received null response from worker.");
        }

        // Sync variants used by the worker host loop. Worker reads block on the pipe
        // until a request arrives or the pipe closes; framing makes both cases unambiguous.
        public void SendResponseSync(WorkerResponse response)
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            WriteFramed(_stream, JsonSerializer.SerializeToUtf8Bytes(response));
        }

        public WorkerRequest? ReceiveRequestSync()
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            byte[]? payload = ReadFramed(_stream);
            return payload == null ? null : JsonSerializer.Deserialize<WorkerRequest>(payload);
        }

        public void Dispose()
        {
            _stream?.Dispose();
        }

        // -----------------------------
        // Framing helpers
        // -----------------------------

        private static async Task WriteFramedAsync(PipeStream stream, byte[] payload, CancellationToken cancellationToken)
        {
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await stream.WriteAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private static void WriteFramed(PipeStream stream, byte[] payload)
        {
            Span<byte> header = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            stream.Write(header);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        private static async Task<byte[]?> ReadFramedAsync(PipeStream stream, CancellationToken cancellationToken)
        {
            byte[] header = new byte[4];
            if (!await TryReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false)) return null;

            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length < 0 || length > MaxMessageBytes)
            {
                throw new InvalidDataException($"Refusing to read framed message of declared size {length}.");
            }
            if (length == 0) return Array.Empty<byte>();

            byte[] payload = new byte[length];
            if (!await TryReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false))
            {
                throw new EndOfStreamException("Pipe closed mid-message.");
            }
            return payload;
        }

        private static byte[]? ReadFramed(PipeStream stream)
        {
            byte[] header = new byte[4];
            if (!TryReadExactly(stream, header)) return null;

            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length < 0 || length > MaxMessageBytes)
            {
                throw new InvalidDataException($"Refusing to read framed message of declared size {length}.");
            }
            if (length == 0) return Array.Empty<byte>();

            byte[] payload = new byte[length];
            if (!TryReadExactly(stream, payload))
            {
                throw new EndOfStreamException("Pipe closed mid-message.");
            }
            return payload;
        }

        private static async Task<bool> TryReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (read == 0) return total == 0 ? false : throw new EndOfStreamException("Pipe closed mid-message.");
                total += read;
            }
            return true;
        }

        private static bool TryReadExactly(Stream stream, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0) return total == 0 ? false : throw new EndOfStreamException("Pipe closed mid-message.");
                total += read;
            }
            return true;
        }
    }

    public class ProcessLauncher : IProcessLauncher
    {
        private readonly string? _workerExecutablePath;
        private readonly string? _extraArguments;

        /// <summary>
        /// Creates a ProcessLauncher that spawns a worker process. The pipe name
        /// is provided per-spawn by the worker client (so that each recycled
        /// worker gets a unique pipe path and avoids EADDRINUSE on Unix).
        /// </summary>
        /// <param name="workerExecutablePath">
        /// Path to worker executable. Pass null to use the current process executable
        /// (which is the typical setup for the GUI launching itself in --mode=worker).
        /// </param>
        /// <param name="extraArguments">
        /// Optional additional command-line arguments appended after --mode=worker --pipe=&lt;name&gt;.
        /// Used by integration tests to pass scenario flags to the test worker.
        /// </param>
        public ProcessLauncher(string? workerExecutablePath = null, string? extraArguments = null)
        {
            _workerExecutablePath = workerExecutablePath;
            _extraArguments = extraArguments;
        }

        public IProcessHandle StartWorker(string pipeName)
        {
            if (string.IsNullOrEmpty(pipeName)) throw new ArgumentException("Pipe name must be provided.", nameof(pipeName));

            string exePath = _workerExecutablePath ?? (Process.GetCurrentProcess().MainModule?.FileName
                             ?? throw new InvalidOperationException("Could not determine current executable path."));

            if (!File.Exists(exePath))
            {
                throw new InvalidOperationException($"Worker executable not found at: {exePath}");
            }

            string arguments = $"--mode=worker --pipe={pipeName}";
            if (!string.IsNullOrEmpty(_extraArguments))
            {
                arguments += " " + _extraArguments;
            }

            var startInfo = new ProcessStartInfo(exePath)
            {
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start worker process.");
            return new ProcessHandle(process);
        }
    }

    public class ProcessHandle : IProcessHandle
    {
        private readonly Process _process;

        public ProcessHandle(Process process)
        {
            _process = process;
            _process.EnableRaisingEvents = true;
            _process.Exited += (s, e) => Exited?.Invoke(this, EventArgs.Empty);
        }

        public int Id => _process.Id;
        public bool HasExited => _process.HasExited;

        public event EventHandler? Exited;

        public void Kill()
        {
            try { if (!_process.HasExited) _process.Kill(true); } catch { }
        }

        public void Dispose()
        {
            _process.Dispose();
        }
    }

    public class DefaultWorkerHealthMonitor : IWorkerHealthMonitor
    {
        public bool IsProcessHealthy(IProcessHandle? handle)
        {
            return handle != null && !handle.HasExited;
        }
    }

    public class DefaultTimeoutProvider : ITimeoutProvider
    {
        private readonly int _defaultTimeoutSeconds;

        public DefaultTimeoutProvider(int defaultTimeoutSeconds)
        {
            _defaultTimeoutSeconds = defaultTimeoutSeconds;
        }

        public TimeSpan GetTimeout(string command)
        {
            return TimeSpan.FromSeconds(_defaultTimeoutSeconds);
        }
    }
}

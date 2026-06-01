using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    public class NamedPipeWorkerTransport : IWorkerTransport
    {
        private readonly string _pipeName;
        private PipeStream? _stream;
        private readonly bool _isServer;

        public NamedPipeWorkerTransport(string pipeName, bool isServer = false)
        {
            _pipeName = pipeName;
            _isServer = isServer;
        }

        public void Connect()
        {
            if (_isServer)
            {
                var serverStream = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                serverStream.WaitForConnection();
                _stream = serverStream;
            }
            else
            {
                var clientStream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                clientStream.Connect(10000);
                _stream = clientStream;
            }
        }

        public async Task SendRequestAsync(WorkerRequest request, CancellationToken cancellationToken)
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            await JsonSerializer.SerializeAsync(_stream, request, cancellationToken: cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }

        public async Task SendResponseAsync(WorkerResponse response, CancellationToken cancellationToken)
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            await JsonSerializer.SerializeAsync(_stream, response, cancellationToken: cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }

        public async Task<WorkerRequest?> ReceiveRequestAsync(CancellationToken cancellationToken)
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            return await JsonSerializer.DeserializeAsync<WorkerRequest>(_stream, cancellationToken: cancellationToken);
        }

        public async Task<WorkerResponse> ReceiveResponseAsync(CancellationToken cancellationToken)
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            var response = await JsonSerializer.DeserializeAsync<WorkerResponse>(_stream, cancellationToken: cancellationToken);
            return response ?? throw new InvalidOperationException("Received null response from worker.");
        }

        // Synchronous methods for STA thread affinity in worker
        public void SendResponseSync(WorkerResponse response)
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            JsonSerializer.Serialize(_stream, response);
            _stream.Flush();
        }

        public WorkerRequest? ReceiveRequestSync()
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            // We need to use a StreamReader or similar because JsonSerializer.Deserialize(Stream)
            // might block differently or we might need to handle EOF.
            // For simplicity in this COM worker, we use the synchronous version.
            try
            {
                return JsonSerializer.Deserialize<WorkerRequest>(_stream);
            }
            catch (JsonException)
            {
                return null;
            }
            catch (EndOfStreamException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            _stream?.Dispose();
        }
    }

    public class ProcessLauncher : IProcessLauncher
    {
        private readonly string _pipeName;

        public ProcessLauncher(string pipeName)
        {
            _pipeName = pipeName;
        }

        public IProcessHandle StartWorker()
        {
            string exePath = Process.GetCurrentProcess().MainModule?.FileName
                             ?? throw new InvalidOperationException("Could not determine current executable path.");

            var startInfo = new ProcessStartInfo(exePath)
            {
                Arguments = $"--mode=worker --pipe={_pipeName}",
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

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Length-prefixed JSON message transport over a named pipe for the
    /// Publisher render worker. The byte-level framing lives in
    /// <see cref="PipeMessageFraming"/> and is shared with the elevated font
    /// worker transport; this class is responsible only for connection setup
    /// and (de)serializing <see cref="WorkerRequest"/>/<see cref="WorkerResponse"/>.
    /// </summary>
    public class NamedPipeWorkerTransport : IWorkerTransport
    {
        private const int DefaultServerWaitTimeoutMs = 30000;
        private const int DefaultClientConnectionTimeoutMs = 10000;
        private const int ConnectionRetryIntervalMs = 100;

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
            await PipeMessageFraming.WriteFramedAsync(_stream, JsonSerializer.SerializeToUtf8Bytes(request), cancellationToken).ConfigureAwait(false);
        }

        public async Task SendResponseAsync(WorkerResponse response, CancellationToken cancellationToken)
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            await PipeMessageFraming.WriteFramedAsync(_stream, JsonSerializer.SerializeToUtf8Bytes(response), cancellationToken).ConfigureAwait(false);
        }

        public async Task<WorkerRequest?> ReceiveRequestAsync(CancellationToken cancellationToken)
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            byte[]? payload = await PipeMessageFraming.ReadFramedAsync(_stream, cancellationToken).ConfigureAwait(false);
            return payload == null ? null : JsonSerializer.Deserialize<WorkerRequest>(payload);
        }

        public async Task<WorkerResponse> ReceiveResponseAsync(CancellationToken cancellationToken)
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            byte[]? payload = await PipeMessageFraming.ReadFramedAsync(_stream, cancellationToken).ConfigureAwait(false);
            if (payload == null) throw new InvalidOperationException("Worker closed the pipe before sending a response.");
            return JsonSerializer.Deserialize<WorkerResponse>(payload)
                   ?? throw new InvalidOperationException("Received null response from worker.");
        }

        // Sync variants used by the worker host loop. Worker reads block on the pipe
        // until a request arrives or the pipe closes; framing makes both cases unambiguous.
        public void SendResponseSync(WorkerResponse response)
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            PipeMessageFraming.WriteFramed(_stream, JsonSerializer.SerializeToUtf8Bytes(response));
        }

        public WorkerRequest? ReceiveRequestSync()
        {
            if (_stream == null) throw new InvalidOperationException("Transport not connected.");
            byte[]? payload = PipeMessageFraming.ReadFramed(_stream);
            return payload == null ? null : JsonSerializer.Deserialize<WorkerRequest>(payload);
        }

        public void Dispose()
        {
            _stream?.Dispose();
        }
    }

    public class ProcessLauncher : IProcessLauncher
    {
        private readonly string? _workerExecutablePath;
        private readonly string? _extraArguments;

        // Kill-on-close job (created once, lazily) holding every worker this
        // launcher spawns. When this (GUI) process exits — cleanly, crash, or
        // force-quit — the OS terminates the workers, the backstop for when the
        // GUI never runs OnClosing / graceful teardown. Best-effort: a failure to
        // create or assign degrades to the graceful teardown layers below.
        private readonly object _jobLock = new object();
        private IntPtr _killOnCloseJob = IntPtr.Zero;
        private bool _jobUnavailable;

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
            TryAssignToKillOnCloseJob(process);
            return new ProcessHandle(process);
        }

        // Assigns the freshly started worker to a process-wide kill-on-close job.
        // Guarded behind OperatingSystem.IsWindows() and try/catch so a failure
        // here never prevents the worker from starting — Layers 1–2 (GUI-exit
        // dispose + graceful teardown) still apply.
        private void TryAssignToKillOnCloseJob(Process process)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                lock (_jobLock)
                {
                    if (_jobUnavailable) return;
                    if (_killOnCloseJob == IntPtr.Zero)
                    {
                        _killOnCloseJob = WorkerJobObject.CreateKillOnCloseJob();
                        if (_killOnCloseJob == IntPtr.Zero) { _jobUnavailable = true; return; }
                    }
                    WorkerJobObject.AssignProcess(_killOnCloseJob, process);
                }
            }
            catch
            {
                _jobUnavailable = true;
            }
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

    /// <summary>
    /// Win32 Job Object helper: a kill-on-close job kills every assigned process
    /// the instant the last handle to the job closes — including when the owning
    /// process crashes or is force-quit, which is exactly the case OnClosing and
    /// graceful teardown can't cover. Two tiers use it: the GUI assigns the worker
    /// process so a GUI crash reaps the worker, and the worker assigns the
    /// DCOM-activated mspub.exe so a worker death reaps Publisher.
    ///
    /// Windows-only by definition; callers must guard with
    /// <see cref="OperatingSystem.IsWindows"/> and treat any failure as a
    /// degrade-to-graceful-teardown, never a crash.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class WorkerJobObject
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        // OpenProcess access rights needed to assign a PID we don't own a handle for.
        private const uint PROCESS_TERMINATE = 0x0001;
        private const uint PROCESS_SET_QUOTA = 0x0100;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// Creates a kill-on-close job object, or returns <see cref="IntPtr.Zero"/>
        /// on any failure. The returned handle must be kept open for the lifetime
        /// of the owning process — when it closes, the OS kills the job's members.
        /// </summary>
        public static IntPtr CreateKillOnCloseJob()
        {
            IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

            int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)len))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            return job;
        }

        /// <summary>Assigns a live process (by its existing handle) to the job.</summary>
        public static bool AssignProcess(IntPtr job, Process process)
            => AssignProcessToJobObject(job, process.Handle);

        /// <summary>
        /// Assigns a process identified only by PID (e.g., DCOM-activated mspub.exe,
        /// which is not a child of this process). Opens a limited handle, assigns,
        /// then closes that handle — the job retains its hold on the process.
        /// </summary>
        public static bool AssignProcessById(IntPtr job, int processId)
        {
            IntPtr hProcess = OpenProcess(PROCESS_TERMINATE | PROCESS_SET_QUOTA, false, processId);
            if (hProcess == IntPtr.Zero) return false;
            try
            {
                return AssignProcessToJobObject(job, hProcess);
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        /// <summary>
        /// Closes a job handle. For a kill-on-close job this terminates any
        /// members still alive — the backstop when graceful release left a
        /// process behind. No-op for <see cref="IntPtr.Zero"/>.
        /// </summary>
        public static void CloseJob(IntPtr job)
        {
            if (job != IntPtr.Zero) CloseHandle(job);
        }
    }
}

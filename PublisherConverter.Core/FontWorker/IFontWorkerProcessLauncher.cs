using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace PublisherConverter.Core.FontWorker
{
    /// <summary>
    /// Launches the font worker process. Abstracted so the worker client can be
    /// tested on Linux with a fake launcher — no real process, no elevation, no
    /// UAC prompt.
    /// </summary>
    public interface IFontWorkerProcessLauncher
    {
        /// <summary>
        /// Starts the current executable in font-worker mode bound to
        /// <paramref name="pipeName"/>. Implementations that require privileged
        /// installation request elevation here (exactly once per launch); the
        /// returned handle represents the spawned process.
        /// </summary>
        IProcessHandle StartFontWorker(string pipeName);
    }

    /// <summary>
    /// Production launcher. Spawns "app.exe --mode=font-worker --pipe=&lt;name&gt;"
    /// elevated via the shell "runas" verb, which triggers a single UAC prompt.
    /// The main application is never relaunched or elevated — only this child.
    ///
    /// Elevation requires UseShellExecute=true, which precludes stdio
    /// redirection; that's fine because the worker communicates exclusively
    /// over the named pipe, not standard streams.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class FontWorkerProcessLauncher : IFontWorkerProcessLauncher
    {
        private readonly string? _workerExecutablePath;
        private readonly bool _elevate;

        /// <param name="workerExecutablePath">
        /// Path to the executable to spawn. Null uses the current process
        /// executable (the GUI launching itself in font-worker mode).
        /// </param>
        /// <param name="elevate">
        /// When true (the default), the worker is launched with the "runas"
        /// verb so it runs elevated. Tests/diagnostics may set this false.
        /// </param>
        public FontWorkerProcessLauncher(string? workerExecutablePath = null, bool elevate = true)
        {
            _workerExecutablePath = workerExecutablePath;
            _elevate = elevate;
        }

        public IProcessHandle StartFontWorker(string pipeName)
        {
            if (string.IsNullOrEmpty(pipeName)) throw new ArgumentException("Pipe name must be provided.", nameof(pipeName));

            string exePath = _workerExecutablePath ?? (Process.GetCurrentProcess().MainModule?.FileName
                             ?? throw new InvalidOperationException("Could not determine current executable path."));

            if (!File.Exists(exePath))
            {
                throw new InvalidOperationException($"Font worker executable not found at: {exePath}");
            }

            var startInfo = new ProcessStartInfo(exePath)
            {
                Arguments = $"{FontWorkerProtocol.FontWorkerModeArgument} --pipe={pipeName}",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            if (_elevate)
            {
                // UAC elevation: requested once here, for this single worker.
                startInfo.UseShellExecute = true;
                startInfo.Verb = "runas";
            }
            else
            {
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
            }

            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start font worker process.");
            return new ProcessHandle(process);
        }
    }
}

using System;
using System.IO;
using System.Linq;
using PublisherConverter.Core;
using PublisherConverter.Core.FontWorker;

namespace PublisherConverter.GUI
{
    public class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // The single executable supports multiple runtime modes. Only the
            // font worker ever runs elevated; the main application stays
            // non-elevated for its entire lifetime and never relaunches itself.
            string mode = ParseArgument(args, "--mode");

            switch (mode)
            {
                case "worker":
                    RunRenderWorkerMode(args);
                    break;

                case "font-worker":
                    RunFontWorkerMode(args);
                    break;

                case "main":
                case "":
                default:
                    RunGuiMode();
                    break;
            }
        }

        private static void RunRenderWorkerMode(string[] args)
        {
            string pipeName = ParseArgument(args, "--pipe");
            if (string.IsNullOrEmpty(pipeName))
            {
                Console.Error.WriteLine("Error: Missing --pipe argument in worker mode.");
                return;
            }

            using var renderer = new PublisherComRenderer();
            var host = new PublisherWorkerHost(pipeName, renderer);
            host.Run();
        }

        /// <summary>
        /// Privileged font worker entry point. Launched by the main process via
        /// the "runas" verb when system-wide font provisioning is required; runs
        /// the font worker host loop and exits. Communicates exclusively over
        /// the named pipe — no UI, no console window.
        /// </summary>
        private static void RunFontWorkerMode(string[] args)
        {
            string pipeName = ParseArgument(args, "--pipe");
            if (string.IsNullOrEmpty(pipeName))
            {
                Console.Error.WriteLine("Error: Missing --pipe argument in font-worker mode.");
                Environment.Exit(FontWorkerHost.FatalExitCode);
                return;
            }

            IStructuredLogger logger = CreateFontWorkerLogger();
            try
            {
                var installer = new WindowsPrivilegedFontInstaller(new DefaultPowerShellRunner(), logger);
                var host = FontWorkerHost.CreateNamedPipeHost(pipeName, installer, logger, new WindowsPrivilegeChecker());
                host.Run();
            }
            catch (Exception ex)
            {
                logger.Error("worker.fatal", null, new System.Collections.Generic.Dictionary<string, object?> { ["error"] = ex.Message });
                Environment.Exit(FontWorkerHost.FatalExitCode);
            }
            finally
            {
                (logger as IDisposable)?.Dispose();
            }
        }

        private static void RunGuiMode()
        {
            var app = new App();
            app.Run(new MainWindow());
        }

        /// <summary>
        /// Builds the worker's structured log sink: JSON Lines to a per-process
        /// file under the local app data folder, suitable for production
        /// troubleshooting. Falls back to a no-op logger if the file can't be
        /// opened (the worker must still run).
        /// </summary>
        private static IStructuredLogger CreateFontWorkerLogger()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MSPub2PDF", "logs");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"font-worker-{Environment.ProcessId}-{DateTime.UtcNow:yyyyMMdd_HHmmss}.jsonl");
                var writer = new StreamWriter(path, append: true) { AutoFlush = true };
                return new JsonLinesStructuredLogger(writer, source: "font-worker", ownsWriter: true);
            }
            catch
            {
                return NullStructuredLogger.Instance;
            }
        }

        private static string ParseArgument(string[] args, string name)
        {
            string? arg = args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.Ordinal));
            if (arg != null)
            {
                return arg.Substring(name.Length + 1);
            }

            int index = Array.IndexOf(args, name);
            if (index != -1 && index < args.Length - 1)
            {
                return args[index + 1];
            }

            return string.Empty;
        }
    }
}

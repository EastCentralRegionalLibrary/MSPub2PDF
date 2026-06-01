using System;
using System.Linq;
using PublisherConverter.Core;

namespace PublisherConverter.TestWorker
{
    /// <summary>
    /// Stand-alone worker executable used by integration tests to exercise the
    /// real process lifecycle (spawn, IPC, render, recycle, shutdown) without
    /// requiring Microsoft Publisher COM. The renderer is selected from a fixed
    /// set of stubs via the --scenario argument.
    /// </summary>
    public class Program
    {
        public static int Main(string[] args)
        {
            string pipeName = ParseArgument(args, "--pipe");
            if (string.IsNullOrEmpty(pipeName))
            {
                Console.Error.WriteLine("Error: Missing --pipe argument.");
                return 1;
            }

            string scenario = ParseArgument(args, "--scenario");
            IDocumentRenderer renderer = scenario switch
            {
                "fail-init"        => new FailingInitRenderer(),
                "fail-render"      => new FailingRenderRenderer(),
                "hang-render"      => new HangingRenderer(),
                "crash-on-render"  => new CrashOnRenderRenderer(),
                "engine-crash"     => new EngineCrashRenderer(),
                _ => new StubDocumentRenderer(),
            };

            try
            {
                using (renderer)
                {
                    var host = new PublisherWorkerHost(pipeName, renderer);
                    host.Run();
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Worker fatal: {ex.Message}");
                return 2;
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
            if (index >= 0 && index < args.Length - 1)
            {
                return args[index + 1];
            }

            return string.Empty;
        }
    }
}

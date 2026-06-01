using System;
using System.Linq;
using PublisherConverter.Core;

namespace PublisherConverter.GUI
{
    public class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Contains("--mode=worker"))
            {
                RunWorkerMode(args);
            }
            else
            {
                RunGuiMode();
            }
        }

        private static void RunWorkerMode(string[] args)
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

        private static void RunGuiMode()
        {
            var app = new App();
            app.Run(new MainWindow());
        }

        private static string ParseArgument(string[] args, string name)
        {
            string? arg = args.FirstOrDefault(a => a.StartsWith(name + "="));
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

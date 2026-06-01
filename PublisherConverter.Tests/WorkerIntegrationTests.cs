using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using PublisherConverter.Core;

namespace PublisherConverter.Tests
{
    /// <summary>
    /// Cross-process integration tests for PublisherWorkerClient and
    /// PublisherLifecycleManager. These tests launch the real
    /// PublisherConverter.TestWorker executable (a small console host that
    /// wraps PublisherWorkerHost with a stub renderer), exercising the
    /// actual ProcessLauncher, ProcessHandle, NamedPipeWorkerTransport and
    /// lifecycle code paths without requiring COM or Microsoft Publisher.
    /// </summary>
    [Trait("Category", "Integration")]
    public class WorkerIntegrationTests : IDisposable
    {
        // Short timeouts so that the failure paths exercised by these tests
        // (worker hang, worker crash, missing executable) wrap up in seconds
        // instead of the 30-second production default.
        private const int ConnectionTimeoutMs = 3000;
        private const int DefaultRequestTimeoutSeconds = 3;

        private readonly string _workspaceDir;
        private readonly List<PublisherWorkerClient> _clients = new();

        public WorkerIntegrationTests()
        {
            _workspaceDir = Path.Combine(Path.GetTempPath(), $"WorkerInt_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_workspaceDir);
        }

        private PublisherWorkerClient CreateClient(string? scenario = null, int? defaultTimeoutSeconds = null)
        {
            string workerExe = TestWorkerLocator.GetExecutable();
            string? extraArgs = scenario != null ? $"--scenario={scenario}" : null;

            var launcher = new ProcessLauncher(workerExe, extraArgs);
            Func<string, IWorkerTransport> transportFactory = pipe => new NamedPipeWorkerTransport(pipe);
            var healthMonitor = new DefaultWorkerHealthMonitor();
            var timeoutProvider = new DefaultTimeoutProvider(defaultTimeoutSeconds ?? DefaultRequestTimeoutSeconds);

            var client = new PublisherWorkerClient(
                launcher,
                transportFactory,
                healthMonitor,
                timeoutProvider,
                connectionTimeoutMs: ConnectionTimeoutMs);
            _clients.Add(client);
            return client;
        }

        [Fact]
        public async Task SpawnsRealWorkerAndConnects()
        {
            var client = CreateClient();

            await client.EnsureWorkerStartedAsync(CancellationToken.None);

            Assert.True(client.IsHealthy);
        }

        [Fact]
        public async Task HealthCheckRoundTripsThroughRealWorker()
        {
            var client = CreateClient();

            var response = await client.SendRequestAsync(
                new WorkerRequest { Command = "health" },
                timeoutSeconds: DefaultRequestTimeoutSeconds,
                CancellationToken.None);

            Assert.True(response.Success);
        }

        [Fact]
        public async Task RenderJobProducesPdfViaRealWorker()
        {
            var client = CreateClient();

            string sourcePath = Path.Combine(_workspaceDir, "source.pub");
            File.WriteAllText(sourcePath, "fake publisher content");
            string targetPath = Path.Combine(_workspaceDir, "output.pdf");

            var response = await client.SendRequestAsync(new WorkerRequest
            {
                Command = "render",
                RenderJob = new RenderJob
                {
                    SourcePubPath = sourcePath,
                    TargetPdfPath = targetPath,
                    RunLinkCheck = false
                }
            }, timeoutSeconds: DefaultRequestTimeoutSeconds, CancellationToken.None);

            Assert.True(response.Success, response.ErrorMessage);
            Assert.NotNull(response.RenderResult);
            Assert.True(File.Exists(targetPath));

            string contents = File.ReadAllText(targetPath);
            Assert.StartsWith("%PDF-", contents);
        }

        [Fact]
        public async Task MultipleSequentialRequestsReuseSameWorkerProcess()
        {
            var client = CreateClient();

            for (int i = 0; i < 5; i++)
            {
                var response = await client.SendRequestAsync(
                    new WorkerRequest { Command = "health" },
                    timeoutSeconds: 10,
                    CancellationToken.None);
                Assert.True(response.Success);
            }

            // Worker should still be alive; we can verify by sending a final
            // request and checking IsHealthy.
            Assert.True(client.IsHealthy);
        }

        [Fact]
        public async Task RecycleTerminatesWorkerThenNextRequestSpawnsNewOne()
        {
            var client = CreateClient();

            await client.EnsureWorkerStartedAsync(CancellationToken.None);
            Assert.True(client.IsHealthy);

            client.RecycleWorker();

            // Wait briefly for the killed process to actually exit so the next
            // bind to the same pipe name can succeed.
            await WaitUntilUnhealthyAsync(client, TimeSpan.FromSeconds(3));
            Assert.False(client.IsHealthy);

            var response = await client.SendRequestAsync(
                new WorkerRequest { Command = "health" },
                timeoutSeconds: DefaultRequestTimeoutSeconds,
                CancellationToken.None);

            Assert.True(response.Success);
            Assert.True(client.IsHealthy);
        }

        [Fact]
        public async Task GracefulShutdownEndsWorkerProcess()
        {
            var client = CreateClient();

            await client.EnsureWorkerStartedAsync(CancellationToken.None);
            Assert.True(client.IsHealthy);

            var response = await client.SendRequestAsync(
                new WorkerRequest { Command = "shutdown" },
                timeoutSeconds: DefaultRequestTimeoutSeconds,
                CancellationToken.None);

            Assert.True(response.Success);

            // Worker process exits after acknowledging shutdown.
            await WaitUntilUnhealthyAsync(client, TimeSpan.FromSeconds(3));
            Assert.False(client.IsHealthy);
        }

        [Fact]
        public async Task RenderFailureFromWorkerSurfacesAsErrorResponse()
        {
            var client = CreateClient(scenario: "fail-render");

            var response = await client.SendRequestAsync(new WorkerRequest
            {
                Command = "render",
                RenderJob = new RenderJob
                {
                    SourcePubPath = Path.Combine(_workspaceDir, "x.pub"),
                    TargetPdfPath = Path.Combine(_workspaceDir, "x.pdf"),
                }
            }, timeoutSeconds: DefaultRequestTimeoutSeconds, CancellationToken.None);

            Assert.False(response.Success);
            Assert.Contains("Simulated render failure", response.ErrorMessage);

            // Worker stays alive after a per-job error and can still serve other commands.
            var healthResponse = await client.SendRequestAsync(
                new WorkerRequest { Command = "health" },
                timeoutSeconds: DefaultRequestTimeoutSeconds,
                CancellationToken.None);
            Assert.True(healthResponse.Success);
        }

        [Fact]
        public async Task HangingRenderTriggersTimeoutAndKillsWorker()
        {
            var client = CreateClient(scenario: "hang-render", defaultTimeoutSeconds: 1);

            var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
                client.SendRequestAsync(new WorkerRequest
                {
                    Command = "render",
                    RenderJob = new RenderJob
                    {
                        SourcePubPath = Path.Combine(_workspaceDir, "x.pub"),
                        TargetPdfPath = Path.Combine(_workspaceDir, "x.pdf"),
                    }
                }, timeoutSeconds: 1, CancellationToken.None));

            Assert.Contains("timed out", ex.Message);

            await WaitUntilUnhealthyAsync(client, TimeSpan.FromSeconds(3));
            Assert.False(client.IsHealthy);
        }

        [Fact]
        public async Task WorkerCrashMidJobSurfacesAsException()
        {
            var client = CreateClient(scenario: "crash-on-render");

            // The worker dies mid-render; the client surfaces this as a pipe-broken
            // / EndOfStream exception rather than a logical error response.
            await Assert.ThrowsAnyAsync<Exception>(() =>
                client.SendRequestAsync(new WorkerRequest
                {
                    Command = "render",
                    RenderJob = new RenderJob
                    {
                        SourcePubPath = Path.Combine(_workspaceDir, "x.pub"),
                        TargetPdfPath = Path.Combine(_workspaceDir, "x.pdf"),
                    }
                }, timeoutSeconds: DefaultRequestTimeoutSeconds, CancellationToken.None));

            await WaitUntilUnhealthyAsync(client, TimeSpan.FromSeconds(3));
            Assert.False(client.IsHealthy);
        }

        [Fact]
        public async Task LifecycleManagerInitializeAgainstRealWorker()
        {
            var client = CreateClient();
            var manager = new PublisherLifecycleManager(client);

            await manager.InitializeAsync(CancellationToken.None);

            Assert.True(client.IsHealthy);

            // Shutdown should release the worker without throwing.
            manager.Shutdown();

            await WaitUntilUnhealthyAsync(client, TimeSpan.FromSeconds(3));
            Assert.False(client.IsHealthy);
        }

        [Fact]
        public async Task LifecycleManagerExecuteRenderingJobPropagatesResult()
        {
            var client = CreateClient();
            var manager = new PublisherLifecycleManager(client);

            await manager.InitializeAsync(CancellationToken.None);

            string sourcePath = Path.Combine(_workspaceDir, "lm.pub");
            File.WriteAllText(sourcePath, "fake");
            string targetPath = Path.Combine(_workspaceDir, "lm.pdf");

            var record = new FileRecord { FileName = "lm.pub" };
            await manager.ExecuteRenderingJobAsync(record, sourcePath, targetPath, runLinkCheck: false, timeoutSeconds: DefaultRequestTimeoutSeconds, CancellationToken.None);

            Assert.True(File.Exists(targetPath));
            Assert.Equal(0, record.MissingAssetsCount);

            manager.Shutdown();
        }

        [Fact]
        public async Task ConnectingToNonexistentWorkerExecutableFailsFast()
        {
            string bogusPath = Path.Combine(_workspaceDir, "does-not-exist.exe");
            var launcher = new ProcessLauncher(bogusPath);

            var client = new PublisherWorkerClient(
                launcher,
                pipe => new NamedPipeWorkerTransport(pipe),
                new DefaultWorkerHealthMonitor(),
                new DefaultTimeoutProvider(DefaultRequestTimeoutSeconds),
                connectionTimeoutMs: ConnectionTimeoutMs);
            _clients.Add(client);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.EnsureWorkerStartedAsync(CancellationToken.None));
        }

        private static async Task WaitUntilUnhealthyAsync(IPublisherWorkerClient client, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!client.IsHealthy) return;
                await Task.Delay(50);
            }
        }

        public void Dispose()
        {
            foreach (var client in _clients)
            {
                try { client.Dispose(); } catch { }
            }

            if (Directory.Exists(_workspaceDir))
            {
                try { Directory.Delete(_workspaceDir, true); } catch { }
            }
        }
    }

    /// <summary>
    /// Locates the compiled PublisherConverter.TestWorker executable so
    /// integration tests can spawn it as a child process. Resolves the path
    /// relative to the test assembly's bin directory by walking up to the
    /// solution root, then reusing the same Configuration/TargetFramework
    /// the test runner was built against.
    /// </summary>
    internal static class TestWorkerLocator
    {
        private const string ProjectName = "PublisherConverter.TestWorker";

        public static string GetExecutable()
        {
            string testBin = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            string? solutionDir = FindSolutionRoot(testBin);
            if (solutionDir == null)
            {
                throw new InvalidOperationException(
                    $"Could not locate the solution root walking up from '{testBin}'.");
            }

            var testBinInfo = new DirectoryInfo(testBin);
            string framework = testBinInfo.Name;
            string config = testBinInfo.Parent?.Name ?? "Debug";

            string exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? $"{ProjectName}.exe"
                : ProjectName;

            string candidate = Path.Combine(solutionDir, ProjectName, "bin", config, framework, exeName);
            if (!File.Exists(candidate))
            {
                throw new FileNotFoundException(
                    $"Test worker executable not found at '{candidate}'. " +
                    $"Make sure '{ProjectName}' is built (it is referenced as a build-only dependency of the test project).");
            }

            return candidate;
        }

        private static string? FindSolutionRoot(string startDir)
        {
            DirectoryInfo? dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                if (dir.GetFiles("*.sln").Any()) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }
    }
}

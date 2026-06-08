using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core;
using PublisherConverter.Core.FontWorker;
using Xunit;

namespace PublisherConverter.Tests.FontWorker
{
    /// <summary>
    /// Exercises the real <see cref="NamedPipeFontWorkerTransport"/> over an
    /// actual OS named pipe with the production transport orientation: the main
    /// process (the <see cref="ElevatedFontWorkerClient"/>) owns the pipe
    /// SERVER, and the worker host connects as the CLIENT. This is the
    /// orientation required by Windows Mandatory Integrity Control so a
    /// Medium-integrity GUI can talk to a High-integrity elevated worker.
    ///
    /// Runs on Linux (named pipes are emulated over Unix domain sockets); the
    /// MIC label semantics are Windows-only, but this guards the wiring and the
    /// real framing/serialization end to end.
    /// </summary>
    public class FontWorkerPipeIntegrationTests
    {
        /// <summary>
        /// Stands in for the elevated process launch: instead of spawning
        /// app.exe --mode=font-worker, it starts an in-process FontWorkerHost
        /// bound to the same pipe name as a named-pipe CLIENT (the worker role).
        /// </summary>
        private sealed class HostSpawningLauncher : IFontWorkerProcessLauncher
        {
            private readonly IPrivilegedFontInstaller _installer;
            public List<Task> HostTasks { get; } = new();
            public List<FakeProcessHandle> Handles { get; } = new();

            public HostSpawningLauncher(IPrivilegedFontInstaller installer) => _installer = installer;

            public IProcessHandle StartFontWorker(string pipeName)
            {
                var host = new FontWorkerHost(
                    new NamedPipeFontWorkerTransport(pipeName, isServer: false), // worker = CLIENT
                    _installer,
                    NullStructuredLogger.Instance,
                    new StaticPrivilegeChecker(true));
                HostTasks.Add(Task.Run(() => host.Run()));
                var handle = new FakeProcessHandle(Handles.Count + 1);
                Handles.Add(handle);
                return handle;
            }
        }

        [Fact]
        public async Task InvertedRoles_MainServerWorkerClient_RoundTripsOverRealPipe()
        {
            var installer = new FakePrivilegedFontInstaller();
            installer.CapabilityOutcomes["Cap.A"] = InstallOutcome.Installed;
            installer.CapabilityOutcomes["Cap.B"] = InstallOutcome.AlreadyPresent;
            var launcher = new HostSpawningLauncher(installer);

            var client = new ElevatedFontWorkerClient(
                launcher,
                pipeName => new NamedPipeFontWorkerTransport(pipeName, isServer: true), // main = SERVER
                new DefaultWorkerHealthMonitor(),
                connectionTimeout: TimeSpan.FromSeconds(10),
                startupTimeout: TimeSpan.FromSeconds(10),
                defaultRequestTimeout: TimeSpan.FromSeconds(10));

            try
            {
                await client.EnsureStartedAsync(CancellationToken.None);
                Assert.True(client.IsHealthy);

                var batch = await client.InstallCapabilitiesBatchAsync(
                    new InstallCapabilitiesBatchRequest { CapabilityNames = new[] { "Cap.A", "Cap.B" } },
                    "corr-int", timeoutSeconds: null, CancellationToken.None);

                Assert.Equal(2, batch.Results.Count);
                Assert.All(batch.Results, r => Assert.True(r.Outcome.IsSuccess()));
                Assert.Contains("corr-int", installer.SeenCorrelationIds);

                var font = await client.InstallFontAsync(
                    new InstallFontRequest { FamilyName = "Fam", SourcePath = "/tmp/x.ttf", FileName = "x.ttf" },
                    "corr-font", null, CancellationToken.None);
                Assert.True(font.Outcome.IsSuccess());

                await client.ShutdownAsync(CancellationToken.None);
                await Task.WhenAll(launcher.HostTasks).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                client.Dispose();
            }
        }

        [Fact]
        public async Task MultipleSequentialRequests_ReuseTheSamePipeConnection()
        {
            var installer = new FakePrivilegedFontInstaller();
            var launcher = new HostSpawningLauncher(installer);

            var client = new ElevatedFontWorkerClient(
                launcher,
                pipeName => new NamedPipeFontWorkerTransport(pipeName, isServer: true),
                new DefaultWorkerHealthMonitor(),
                connectionTimeout: TimeSpan.FromSeconds(10),
                startupTimeout: TimeSpan.FromSeconds(10),
                defaultRequestTimeout: TimeSpan.FromSeconds(10));

            try
            {
                await client.EnsureStartedAsync(CancellationToken.None);

                for (int i = 0; i < 4; i++)
                {
                    var resp = await client.InstallCapabilityAsync(
                        new InstallCapabilityRequest { CapabilityName = $"Cap.{i}" }, $"c{i}", null, CancellationToken.None);
                    Assert.True(resp.Outcome.IsSuccess());
                }

                Assert.True(client.IsHealthy);
                Assert.Single(launcher.Handles); // one worker, reused

                await client.ShutdownAsync(CancellationToken.None);
                await Task.WhenAll(launcher.HostTasks).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}

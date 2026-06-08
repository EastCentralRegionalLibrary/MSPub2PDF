using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core;
using PublisherConverter.Core.FontWorker;
using Xunit;

namespace PublisherConverter.Tests.FontWorker
{
    public class ElevatedFontWorkerClientTests
    {
        private sealed class ProgrammableTransport : FakeFontWorkerTransport
        {
            public int HangAfterResponses { get; set; } = int.MaxValue;
            public int ThrowAfterResponses { get; set; } = int.MaxValue;
            private int _received;

            public override async Task<FontWorkerResponse> ReceiveResponseAsync(CancellationToken cancellationToken)
            {
                _received++;
                if (_received > ThrowAfterResponses) throw new IOException("pipe broke");
                if (_received > HangAfterResponses) await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return await base.ReceiveResponseAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private static ElevatedFontWorkerClient CreateClient(
            FakeFontWorkerProcessLauncher launcher,
            IFontWorkerTransport transport,
            TimeSpan? startupTimeout = null,
            TimeSpan? requestTimeout = null,
            IStructuredLogger? logger = null)
        {
            return new ElevatedFontWorkerClient(
                launcher,
                _ => transport,
                new FakeWorkerHealthMonitor(),
                logger,
                connectionTimeout: TimeSpan.FromSeconds(5),
                startupTimeout: startupTimeout ?? TimeSpan.FromSeconds(5),
                defaultRequestTimeout: requestTimeout ?? TimeSpan.FromSeconds(5));
        }

        [Fact]
        public async Task EnsureStarted_LaunchesOneProcessAndHealthChecks()
        {
            var launcher = new FakeFontWorkerProcessLauncher();
            var transport = new FakeFontWorkerTransport();
            var client = CreateClient(launcher, transport);

            await client.EnsureStartedAsync(CancellationToken.None);

            Assert.Single(launcher.CreatedProcesses);
            Assert.True(transport.Connected);
            Assert.True(client.IsHealthy);
            // Startup sent exactly one health-check request.
            Assert.Single(transport.SentRequests);
            Assert.Equal(FontWorkerMessageType.HealthCheck, transport.SentRequests[0].Type);
        }

        [Fact]
        public async Task EnsureStarted_IsIdempotentForHealthyWorker()
        {
            var launcher = new FakeFontWorkerProcessLauncher();
            var client = CreateClient(launcher, new FakeFontWorkerTransport());

            await client.EnsureStartedAsync(CancellationToken.None);
            await client.EnsureStartedAsync(CancellationToken.None);
            await client.EnsureStartedAsync(CancellationToken.None);

            Assert.Single(launcher.CreatedProcesses);
        }

        [Fact]
        public async Task EnsureStarted_LaunchFailure_ThrowsStartupException()
        {
            var launcher = new FakeFontWorkerProcessLauncher { ThrowOnStart = new InvalidOperationException("no exe") };
            var client = CreateClient(launcher, new FakeFontWorkerTransport());

            await Assert.ThrowsAsync<FontWorkerStartupException>(() => client.EnsureStartedAsync(CancellationToken.None));
            Assert.False(client.IsHealthy);
        }

        [Fact]
        public async Task EnsureStarted_UnhealthyStartupHealthCheck_ThrowsAndKillsWorker()
        {
            var launcher = new FakeFontWorkerProcessLauncher();
            var transport = new FakeFontWorkerTransport();
            transport.Responses.Enqueue(new FontWorkerResponse
            {
                Type = FontWorkerMessageType.HealthCheck,
                HealthCheck = new HealthCheckResponse { Healthy = false },
            });
            var client = CreateClient(launcher, transport);

            await Assert.ThrowsAsync<FontWorkerStartupException>(() => client.EnsureStartedAsync(CancellationToken.None));
            Assert.True(launcher.CreatedProcesses[0].Killed);
            Assert.False(client.IsHealthy);
        }

        [Fact]
        public async Task EnsureStarted_StartupHealthTimeout_ThrowsStartupException()
        {
            var launcher = new FakeFontWorkerProcessLauncher();
            var transport = new ProgrammableTransport { HangAfterResponses = 0 }; // hang on the startup health check
            var client = CreateClient(launcher, transport, startupTimeout: TimeSpan.FromMilliseconds(80));

            await Assert.ThrowsAsync<FontWorkerStartupException>(() => client.EnsureStartedAsync(CancellationToken.None));
            Assert.True(launcher.CreatedProcesses[0].Killed);
            Assert.False(client.IsHealthy);
        }

        [Fact]
        public async Task Request_Timeout_TearsDownWorkerAndReturnsStructuredFailure()
        {
            var launcher = new FakeFontWorkerProcessLauncher();
            var transport = new ProgrammableTransport { HangAfterResponses = 1 }; // startup ok, then hang
            var client = CreateClient(launcher, transport, requestTimeout: TimeSpan.FromMilliseconds(80));

            await client.EnsureStartedAsync(CancellationToken.None);

            var response = await client.InstallCapabilityAsync(
                new InstallCapabilityRequest { CapabilityName = "Cap.A" }, "corr", timeoutSeconds: null, CancellationToken.None);

            Assert.Equal(InstallOutcome.Failed, response.Outcome);
            Assert.False(client.IsHealthy); // worker marked unhealthy
            Assert.True(launcher.CreatedProcesses[0].Killed);
        }

        [Fact]
        public async Task Request_CommunicationFailure_TearsDownAndReturnsFailure()
        {
            var launcher = new FakeFontWorkerProcessLauncher();
            var transport = new ProgrammableTransport { ThrowAfterResponses = 1 }; // startup ok, op throws
            var client = CreateClient(launcher, transport);

            await client.EnsureStartedAsync(CancellationToken.None);

            var response = await client.InstallFontAsync(
                new InstallFontRequest { FamilyName = "Fam", SourcePath = "/tmp/a.ttf", FileName = "a.ttf" }, "c", null, CancellationToken.None);

            Assert.Equal(InstallOutcome.Failed, response.Outcome);
            Assert.False(client.IsHealthy);
        }

        [Fact]
        public async Task Request_WithoutWorker_ReturnsFailureWithoutLaunching()
        {
            var launcher = new FakeFontWorkerProcessLauncher();
            var client = CreateClient(launcher, new FakeFontWorkerTransport());

            // No EnsureStartedAsync — operations must NOT auto-launch.
            var response = await client.InstallCapabilityAsync(
                new InstallCapabilityRequest { CapabilityName = "Cap" }, "c", null, CancellationToken.None);

            Assert.Equal(InstallOutcome.Failed, response.Outcome);
            Assert.Empty(launcher.CreatedProcesses);
        }

        [Fact]
        public async Task Batch_HappyPath_ReturnsResultsAndPropagatesCorrelationId()
        {
            var launcher = new FakeFontWorkerProcessLauncher();
            var transport = new FakeFontWorkerTransport();
            var client = CreateClient(launcher, transport);
            await client.EnsureStartedAsync(CancellationToken.None);

            var response = await client.InstallCapabilitiesBatchAsync(
                new InstallCapabilitiesBatchRequest { CapabilityNames = new[] { "A", "B" } }, "batch-corr", null, CancellationToken.None);

            Assert.Equal(2, response.Results.Count);
            var opRequest = transport.SentRequests.Last();
            Assert.Equal(FontWorkerMessageType.InstallCapabilitiesBatch, opRequest.Type);
            Assert.Equal("batch-corr", opRequest.CorrelationId);
        }

        [Fact]
        public async Task ConcurrentEnsureStarted_LaunchesOnlyOneProcess()
        {
            var launcher = new FakeFontWorkerProcessLauncher();
            var client = CreateClient(launcher, new FakeFontWorkerTransport());

            var tasks = Enumerable.Range(0, 6).Select(_ => client.EnsureStartedAsync(CancellationToken.None)).ToArray();
            await Task.WhenAll(tasks);

            Assert.Single(launcher.CreatedProcesses);
        }

        [Fact]
        public async Task Shutdown_SendsShutdownAndReleasesWorker()
        {
            var launcher = new FakeFontWorkerProcessLauncher();
            var transport = new FakeFontWorkerTransport();
            var client = CreateClient(launcher, transport);
            await client.EnsureStartedAsync(CancellationToken.None);

            await client.ShutdownAsync(CancellationToken.None);

            Assert.Contains(transport.SentRequests, r => r.Type == FontWorkerMessageType.Shutdown);
            Assert.False(client.IsHealthy);
            Assert.True(launcher.CreatedProcesses[0].Killed);
        }

        [Fact]
        public async Task Dispose_RejectsFurtherRequests()
        {
            var launcher = new FakeFontWorkerProcessLauncher();
            var client = CreateClient(launcher, new FakeFontWorkerTransport());
            await client.EnsureStartedAsync(CancellationToken.None);

            client.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                client.InstallCapabilityAsync(new InstallCapabilityRequest { CapabilityName = "X" }, "c", null, CancellationToken.None));
        }
    }
}

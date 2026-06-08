using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FontWorker;
using Xunit;

namespace PublisherConverter.Tests.FontWorker
{
    /// <summary>
    /// End-to-end exercise of the real <see cref="ElevatedFontWorkerClient"/>
    /// and <see cref="FontWorkerHost"/> wired together over an in-memory
    /// transport pair (no process, no pipe, no elevation). Validates that the
    /// strongly-typed request/response pipeline, correlation IDs, batch install,
    /// idempotency, and clean shutdown all work across the seam.
    /// </summary>
    public class FontWorkerLoopbackTests
    {
        private sealed class Channel
        {
            public readonly BlockingCollection<FontWorkerRequest> Requests = new();
            public readonly BlockingCollection<FontWorkerResponse> Responses = new();
        }

        private sealed class LoopbackClientTransport : IFontWorkerTransport
        {
            private readonly Channel _ch;
            public LoopbackClientTransport(Channel ch) => _ch = ch;
            public Task ConnectAsync(CancellationToken ct, int? timeoutMs = null) => Task.CompletedTask;
            public Task SendRequestAsync(FontWorkerRequest request, CancellationToken ct) { _ch.Requests.Add(request, ct); return Task.CompletedTask; }
            public Task<FontWorkerResponse> ReceiveResponseAsync(CancellationToken ct) => Task.Run(() => _ch.Responses.Take(ct), ct);
            public void Connect() { }
            public FontWorkerRequest? ReceiveRequestSync() => throw new NotSupportedException();
            public void SendResponseSync(FontWorkerResponse response) => throw new NotSupportedException();
            public void Dispose() { }
        }

        private sealed class LoopbackServerTransport : IFontWorkerTransport
        {
            private readonly Channel _ch;
            public LoopbackServerTransport(Channel ch) => _ch = ch;
            public void Connect() { }
            public FontWorkerRequest? ReceiveRequestSync()
            {
                try { return _ch.Requests.Take(); }
                catch (InvalidOperationException) { return null; }
            }
            public void SendResponseSync(FontWorkerResponse response) => _ch.Responses.Add(response);
            public Task ConnectAsync(CancellationToken ct, int? timeoutMs = null) => throw new NotSupportedException();
            public Task SendRequestAsync(FontWorkerRequest r, CancellationToken ct) => throw new NotSupportedException();
            public Task<FontWorkerResponse> ReceiveResponseAsync(CancellationToken ct) => throw new NotSupportedException();
            public void Dispose() { }
        }

        [Fact]
        public async Task FullPipeline_HealthBatchFontAndShutdown()
        {
            var channel = new Channel();
            var installer = new FakePrivilegedFontInstaller();
            installer.CapabilityOutcomes["Cap.A"] = InstallOutcome.Installed;
            installer.CapabilityOutcomes["Cap.B"] = InstallOutcome.AlreadyPresent;

            var host = new FontWorkerHost(
                new LoopbackServerTransport(channel),
                installer,
                NullStructuredLogger.Instance,
                new StaticPrivilegeChecker(true));
            var hostTask = Task.Run(() => host.Run());

            var launcher = new FakeFontWorkerProcessLauncher();
            var client = new ElevatedFontWorkerClient(
                launcher,
                _ => new LoopbackClientTransport(channel),
                new FakeWorkerHealthMonitor(),
                startupTimeout: TimeSpan.FromSeconds(5),
                defaultRequestTimeout: TimeSpan.FromSeconds(5));

            try
            {
                await client.EnsureStartedAsync(CancellationToken.None);
                Assert.True(client.IsHealthy);

                var batch = await client.InstallCapabilitiesBatchAsync(
                    new InstallCapabilitiesBatchRequest { CapabilityNames = new[] { "Cap.A", "Cap.B" } },
                    "corr-batch", timeoutSeconds: null, CancellationToken.None);

                Assert.Equal(2, batch.Results.Count);
                Assert.All(batch.Results, r => Assert.True(r.Outcome.IsSuccess()));
                Assert.Contains("corr-batch", installer.SeenCorrelationIds);

                var font = await client.InstallFontAsync(
                    new InstallFontRequest { FamilyName = "Fam", SourcePath = "/tmp/x.ttf", FileName = "x.ttf" },
                    "corr-font", null, CancellationToken.None);
                Assert.True(font.Outcome.IsSuccess());

                await client.ShutdownAsync(CancellationToken.None);

                await hostTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(hostTask.IsCompletedSuccessfully);
            }
            finally
            {
                client.Dispose();
            }
        }

        [Fact]
        public async Task Idempotency_RepeatedCapabilityInstall_ReportsAlreadyPresent()
        {
            var channel = new Channel();
            // A stateful installer: first install reports Installed, subsequent AlreadyPresent.
            var installer = new IdempotentInstaller();

            var host = new FontWorkerHost(new LoopbackServerTransport(channel), installer, NullStructuredLogger.Instance, new StaticPrivilegeChecker(true));
            var hostTask = Task.Run(() => host.Run());

            var client = new ElevatedFontWorkerClient(
                new FakeFontWorkerProcessLauncher(),
                _ => new LoopbackClientTransport(channel),
                new FakeWorkerHealthMonitor(),
                startupTimeout: TimeSpan.FromSeconds(5),
                defaultRequestTimeout: TimeSpan.FromSeconds(5));

            try
            {
                await client.EnsureStartedAsync(CancellationToken.None);

                var first = await client.InstallCapabilityAsync(new InstallCapabilityRequest { CapabilityName = "Cap.X" }, "c1", null, CancellationToken.None);
                var second = await client.InstallCapabilityAsync(new InstallCapabilityRequest { CapabilityName = "Cap.X" }, "c2", null, CancellationToken.None);

                Assert.Equal(InstallOutcome.Installed, first.Outcome);
                Assert.Equal(InstallOutcome.AlreadyPresent, second.Outcome);

                await client.ShutdownAsync(CancellationToken.None);
                await hostTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                client.Dispose();
            }
        }

        private sealed class IdempotentInstaller : IPrivilegedFontInstaller
        {
            private readonly System.Collections.Generic.HashSet<string> _installed = new(StringComparer.OrdinalIgnoreCase);

            public Task<InstallCapabilityResponse> InstallCapabilityAsync(InstallCapabilityRequest request, string correlationId, CancellationToken cancellationToken)
            {
                var outcome = _installed.Add(request.CapabilityName) ? InstallOutcome.Installed : InstallOutcome.AlreadyPresent;
                return Task.FromResult(new InstallCapabilityResponse { CapabilityName = request.CapabilityName, Outcome = outcome });
            }

            public async Task<InstallCapabilitiesBatchResponse> InstallCapabilitiesBatchAsync(InstallCapabilitiesBatchRequest request, string correlationId, CancellationToken cancellationToken)
            {
                var results = new System.Collections.Generic.List<InstallCapabilityResponse>();
                foreach (var n in request.CapabilityNames)
                    results.Add(await InstallCapabilityAsync(new InstallCapabilityRequest { CapabilityName = n }, correlationId, cancellationToken));
                return new InstallCapabilitiesBatchResponse { Results = results };
            }

            public Task<InstallFontResponse> InstallFontAsync(InstallFontRequest request, string correlationId, CancellationToken cancellationToken)
                => Task.FromResult(new InstallFontResponse { FamilyName = request.FamilyName, Outcome = InstallOutcome.Installed });
        }
    }
}

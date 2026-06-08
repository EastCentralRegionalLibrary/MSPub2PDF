using System;
using System.Collections.Generic;
using System.Threading;
using PublisherConverter.Core.FontWorker;
using Xunit;

namespace PublisherConverter.Tests.FontWorker
{
    /// <summary>
    /// Drives <see cref="FontWorkerHost"/> through a scripted in-memory
    /// transport: the test queues the requests the worker should "receive" and
    /// captures every response it sends. No pipes, no process, no elevation.
    /// </summary>
    public class FontWorkerHostTests
    {
        /// <summary>Server-side fake transport that feeds a fixed request script and records responses.</summary>
        private sealed class ScriptedServerTransport : IFontWorkerTransport
        {
            private readonly Queue<FontWorkerRequest?> _incoming;
            public List<FontWorkerResponse> Sent { get; } = new();
            public bool Connected { get; private set; }

            public ScriptedServerTransport(params FontWorkerRequest?[] requests)
            {
                _incoming = new Queue<FontWorkerRequest?>(requests);
            }

            public void Connect() => Connected = true;

            public FontWorkerRequest? ReceiveRequestSync()
                => _incoming.Count > 0 ? _incoming.Dequeue() : null; // null => pipe closed

            public void SendResponseSync(FontWorkerResponse response) => Sent.Add(response);

            // Client-side members unused by the host.
            public System.Threading.Tasks.Task ConnectAsync(CancellationToken ct, int? timeoutMs = null) => throw new NotSupportedException();
            public System.Threading.Tasks.Task SendRequestAsync(FontWorkerRequest r, CancellationToken ct) => throw new NotSupportedException();
            public System.Threading.Tasks.Task<FontWorkerResponse> ReceiveResponseAsync(CancellationToken ct) => throw new NotSupportedException();
            public void Dispose() { }
        }

        private static FontWorkerRequest Req(FontWorkerMessageType type, string correlationId = "cid", Action<FontWorkerRequest>? _ = null)
            => new FontWorkerRequest { Type = type, CorrelationId = correlationId };

        [Fact]
        public void HealthCheck_ReportsHealthyWithVersionAndElevation()
        {
            var transport = new ScriptedServerTransport(
                new FontWorkerRequest { Type = FontWorkerMessageType.HealthCheck, CorrelationId = "h1", HealthCheck = new HealthCheckRequest() },
                null);
            var host = new FontWorkerHost(transport, new FakePrivilegedFontInstaller(), NullStructuredLogger.Instance, new StaticPrivilegeChecker(true));

            host.Run();

            var resp = Assert.Single(transport.Sent);
            Assert.Equal("h1", resp.CorrelationId);
            Assert.True(resp.HealthCheck!.Healthy);
            Assert.True(resp.HealthCheck.Elevated);
            Assert.Equal(FontWorkerProtocol.Version, resp.HealthCheck.WorkerVersion);
        }

        [Fact]
        public void HealthCheck_SurfacesNonElevatedWorker()
        {
            var transport = new ScriptedServerTransport(
                new FontWorkerRequest { Type = FontWorkerMessageType.HealthCheck, CorrelationId = "h", HealthCheck = new HealthCheckRequest() },
                null);
            var host = new FontWorkerHost(transport, new FakePrivilegedFontInstaller(), NullStructuredLogger.Instance, new StaticPrivilegeChecker(false));

            host.Run();

            Assert.False(transport.Sent[0].HealthCheck!.Elevated);
        }

        [Fact]
        public void InstallCapability_DelegatesToInstallerAndEchoesCorrelationId()
        {
            var installer = new FakePrivilegedFontInstaller();
            installer.CapabilityOutcomes["Cap.A"] = InstallOutcome.Installed;
            var transport = new ScriptedServerTransport(
                new FontWorkerRequest { Type = FontWorkerMessageType.InstallCapability, CorrelationId = "corr-1", InstallCapability = new InstallCapabilityRequest { CapabilityName = "Cap.A" } },
                null);
            var host = new FontWorkerHost(transport, installer, NullStructuredLogger.Instance);

            host.Run();

            Assert.Contains("Cap.A", installer.InstalledCapabilities);
            Assert.Contains("corr-1", installer.SeenCorrelationIds);
            var resp = Assert.Single(transport.Sent);
            Assert.Equal("corr-1", resp.CorrelationId);
            Assert.Equal(InstallOutcome.Installed, resp.InstallCapability!.Outcome);
        }

        [Fact]
        public void InstallCapabilitiesBatch_ReturnsPerItemResults()
        {
            var installer = new FakePrivilegedFontInstaller();
            installer.CapabilityOutcomes["A"] = InstallOutcome.Installed;
            installer.CapabilityOutcomes["B"] = InstallOutcome.AlreadyPresent;
            var transport = new ScriptedServerTransport(
                new FontWorkerRequest
                {
                    Type = FontWorkerMessageType.InstallCapabilitiesBatch,
                    CorrelationId = "b",
                    InstallCapabilitiesBatch = new InstallCapabilitiesBatchRequest { CapabilityNames = new[] { "A", "B" } },
                },
                null);
            var host = new FontWorkerHost(transport, installer, NullStructuredLogger.Instance);

            host.Run();

            var results = transport.Sent[0].InstallCapabilitiesBatch!.Results;
            Assert.Equal(2, results.Count);
        }

        [Fact]
        public void InstallFont_DelegatesToInstaller()
        {
            var installer = new FakePrivilegedFontInstaller();
            var transport = new ScriptedServerTransport(
                new FontWorkerRequest { Type = FontWorkerMessageType.InstallFont, CorrelationId = "f", InstallFont = new InstallFontRequest { FamilyName = "Noto Serif KR", SourcePath = "/tmp/x.ttf", FileName = "x.ttf" } },
                null);
            var host = new FontWorkerHost(transport, installer, NullStructuredLogger.Instance);

            host.Run();

            Assert.Contains("Noto Serif KR", installer.InstalledFonts);
            Assert.Equal(InstallOutcome.Installed, transport.Sent[0].InstallFont!.Outcome);
        }

        [Fact]
        public void FailureIsolation_HandlerExceptionBecomesFailureAndWorkerKeepsServing()
        {
            var installer = new FakePrivilegedFontInstaller
            {
                ThrowForCapability = cap => cap == "Bad" ? new InvalidOperationException("kaboom") : null,
            };
            var transport = new ScriptedServerTransport(
                new FontWorkerRequest { Type = FontWorkerMessageType.InstallCapability, CorrelationId = "1", InstallCapability = new InstallCapabilityRequest { CapabilityName = "Bad" } },
                new FontWorkerRequest { Type = FontWorkerMessageType.HealthCheck, CorrelationId = "2", HealthCheck = new HealthCheckRequest() },
                null);
            var host = new FontWorkerHost(transport, installer, NullStructuredLogger.Instance);

            host.Run();

            // First request failed structurally; worker stayed alive and served the health check.
            Assert.Equal(2, transport.Sent.Count);
            Assert.Equal(InstallOutcome.Failed, transport.Sent[0].InstallCapability!.Outcome);
            Assert.Contains("kaboom", transport.Sent[0].InstallCapability!.Detail);
            Assert.True(transport.Sent[1].HealthCheck!.Healthy);
        }

        [Fact]
        public void Shutdown_AcknowledgesAndStopsLoop()
        {
            var transport = new ScriptedServerTransport(
                new FontWorkerRequest { Type = FontWorkerMessageType.Shutdown, CorrelationId = "s", Shutdown = new ShutdownRequest() },
                // A second request that must never be processed because the loop ended.
                new FontWorkerRequest { Type = FontWorkerMessageType.HealthCheck, CorrelationId = "after", HealthCheck = new HealthCheckRequest() });
            var host = new FontWorkerHost(transport, new FakePrivilegedFontInstaller(), NullStructuredLogger.Instance);

            host.Run();

            var resp = Assert.Single(transport.Sent);
            Assert.True(resp.Shutdown!.Acknowledged);
        }

        [Fact]
        public void ProtocolVersionMismatch_ReturnsEnvelopeError()
        {
            var transport = new ScriptedServerTransport(
                new FontWorkerRequest { Type = FontWorkerMessageType.HealthCheck, ProtocolVersion = "0.1", CorrelationId = "v", HealthCheck = new HealthCheckRequest() },
                null);
            var host = new FontWorkerHost(transport, new FakePrivilegedFontInstaller(), NullStructuredLogger.Instance);

            host.Run();

            Assert.False(transport.Sent[0].EnvelopeSuccess);
            Assert.Contains("protocol version", transport.Sent[0].EnvelopeError);
        }

        [Fact]
        public void MissingPayload_ReturnsEnvelopeError()
        {
            var transport = new ScriptedServerTransport(
                new FontWorkerRequest { Type = FontWorkerMessageType.InstallCapability, CorrelationId = "m" }, // no payload
                null);
            var host = new FontWorkerHost(transport, new FakePrivilegedFontInstaller(), NullStructuredLogger.Instance);

            host.Run();

            Assert.False(transport.Sent[0].EnvelopeSuccess);
        }

        [Fact]
        public void Logging_EmitsStartupRequestResponseAndShutdownWithCorrelation()
        {
            var logger = new InMemoryStructuredLogger();
            var transport = new ScriptedServerTransport(
                new FontWorkerRequest { Type = FontWorkerMessageType.InstallCapability, CorrelationId = "log-corr", InstallCapability = new InstallCapabilityRequest { CapabilityName = "A" } },
                new FontWorkerRequest { Type = FontWorkerMessageType.Shutdown, CorrelationId = "sd", Shutdown = new ShutdownRequest() });
            var host = new FontWorkerHost(transport, new FakePrivilegedFontInstaller(), logger);

            host.Run();

            var events = new List<string>();
            foreach (var e in logger.Entries) events.Add(e.EventName);
            Assert.Contains("worker.started", events);
            Assert.Contains("worker.request", events);
            Assert.Contains("worker.response", events);
            Assert.Contains("worker.shutdown_requested", events);
            Assert.Contains(logger.Entries, e => e.CorrelationId == "log-corr");
        }
    }
}

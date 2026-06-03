using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using PublisherConverter.Core;

namespace PublisherConverter.Tests
{
    /// <summary>
    /// Unit tests that exercise the orchestration logic in PublisherWorkerClient
    /// and PublisherLifecycleManager using in-process fakes for the transport,
    /// process launcher, and health monitor. These tests stay fast and
    /// deterministic; the cross-process behavior is exercised by
    /// WorkerIntegrationTests.
    /// </summary>
    public class WorkerOrchestrationTests
    {
        // -----------------------------
        // PublisherWorkerClient
        // -----------------------------

        [Fact]
        public async Task WorkerClient_StartsWorkerLazilyOnFirstRequest()
        {
            var launcher = new FakeProcessLauncher();
            var transport = new FakeWorkerTransport();
            var client = new PublisherWorkerClient(
                launcher,
                _ => transport,
                new FakeWorkerHealthMonitor(),
                new FakeTimeoutProvider());

            Assert.Empty(launcher.CreatedProcesses);

            var response = await client.SendRequestAsync(
                new WorkerRequest { Command = "render" },
                timeoutSeconds: 10,
                CancellationToken.None);

            Assert.True(response.Success);
            Assert.Single(launcher.CreatedProcesses);
            Assert.True(transport.Connected);
            Assert.Single(transport.SentRequests);
            Assert.Equal("render", transport.SentRequests[0].Command);
        }

        [Fact]
        public async Task WorkerClient_DoesNotRestartHealthyWorker()
        {
            var launcher = new FakeProcessLauncher();
            var transport = new FakeWorkerTransport();
            var client = new PublisherWorkerClient(
                launcher,
                _ => transport,
                new FakeWorkerHealthMonitor(),
                new FakeTimeoutProvider());

            await client.EnsureWorkerStartedAsync(CancellationToken.None);
            await client.EnsureWorkerStartedAsync(CancellationToken.None);
            await client.EnsureWorkerStartedAsync(CancellationToken.None);

            Assert.Single(launcher.CreatedProcesses);
        }

        [Fact]
        public async Task WorkerClient_RecycleKillsOldProcessAndNextRequestStartsNew()
        {
            var launcher = new FakeProcessLauncher();
            var transports = new Queue<FakeWorkerTransport>();
            transports.Enqueue(new FakeWorkerTransport());
            transports.Enqueue(new FakeWorkerTransport());

            var client = new PublisherWorkerClient(
                launcher,
                _ => transports.Dequeue(),
                new FakeWorkerHealthMonitor(),
                new FakeTimeoutProvider());

            await client.EnsureWorkerStartedAsync(CancellationToken.None);
            Assert.Single(launcher.CreatedProcesses);

            client.RecycleWorker();
            Assert.True(launcher.CreatedProcesses[0].Killed);

            await client.EnsureWorkerStartedAsync(CancellationToken.None);
            Assert.Equal(2, launcher.CreatedProcesses.Count);
            Assert.False(launcher.CreatedProcesses[1].Killed);
        }

        [Fact]
        public async Task WorkerClient_TimeoutKillsWorkerAndThrowsTimeoutException()
        {
            var launcher = new FakeProcessLauncher();
            var hangingTransport = new HangingWorkerTransport();
            var timeoutProvider = new FakeTimeoutProvider { Timeout = TimeSpan.FromMilliseconds(20) };

            var client = new PublisherWorkerClient(
                launcher,
                _ => hangingTransport,
                new FakeWorkerHealthMonitor(),
                timeoutProvider);

            var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
                client.SendRequestAsync(new WorkerRequest { Command = "render" }, timeoutSeconds: null, CancellationToken.None));

            Assert.Contains("timed out", ex.Message);
            Assert.Single(launcher.CreatedProcesses);
            Assert.True(launcher.CreatedProcesses[0].Killed);
        }

        [Fact]
        public async Task WorkerClient_TransportExceptionKillsWorkerAndRethrows()
        {
            var launcher = new FakeProcessLauncher();
            var failingTransport = new FailingWorkerTransport();
            var client = new PublisherWorkerClient(
                launcher,
                _ => failingTransport,
                new FakeWorkerHealthMonitor(),
                new FakeTimeoutProvider());

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.SendRequestAsync(new WorkerRequest { Command = "render" }, timeoutSeconds: 10, CancellationToken.None));

            Assert.True(launcher.CreatedProcesses[0].Killed);
        }

        [Fact]
        public async Task WorkerClient_LogicalFailureDoesNotRecycleWorker()
        {
            var launcher = new FakeProcessLauncher();
            var transport = new FakeWorkerTransport();
            transport.Responses.Enqueue(new WorkerResponse { Success = false, ErrorMessage = "render failed" });

            var client = new PublisherWorkerClient(
                launcher,
                _ => transport,
                new FakeWorkerHealthMonitor(),
                new FakeTimeoutProvider());

            var response = await client.SendRequestAsync(
                new WorkerRequest { Command = "render" },
                timeoutSeconds: 10,
                CancellationToken.None);

            Assert.False(response.Success);
            Assert.False(launcher.CreatedProcesses[0].Killed);
        }

        [Fact]
        public async Task WorkerClient_AfterDisposeRejectsNewRequests()
        {
            var launcher = new FakeProcessLauncher();
            var client = new PublisherWorkerClient(
                launcher,
                _ => new FakeWorkerTransport(),
                new FakeWorkerHealthMonitor(),
                new FakeTimeoutProvider());

            client.Dispose();

            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                client.SendRequestAsync(new WorkerRequest { Command = "health" }, timeoutSeconds: 1, CancellationToken.None));
        }

        [Fact]
        public async Task WorkerClient_ConcurrentStartsLaunchOnlyOneProcess()
        {
            var launcher = new FakeProcessLauncher();
            var transport = new FakeWorkerTransport();

            var client = new PublisherWorkerClient(
                launcher,
                _ => transport,
                new FakeWorkerHealthMonitor(),
                new FakeTimeoutProvider());

            var tasks = new Task[4];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = client.EnsureWorkerStartedAsync(CancellationToken.None);
            }
            await Task.WhenAll(tasks);

            Assert.Single(launcher.CreatedProcesses);
        }

        // -----------------------------
        // PublisherLifecycleManager
        // -----------------------------

        [Fact]
        public void LifecycleManager_TracksConsecutiveFailures()
        {
            var manager = new PublisherLifecycleManager(new FakePublisherWorkerClient(), maxConsecutiveFailures: 3);

            Assert.False(manager.RecordBatchFailure());
            Assert.False(manager.RecordBatchFailure());
            Assert.True(manager.RecordBatchFailure());
            Assert.Equal(3, manager.ConsecutiveFailures);
        }

        [Fact]
        public void LifecycleManager_SuccessResetsFailureCount()
        {
            var manager = new PublisherLifecycleManager(new FakePublisherWorkerClient(), maxConsecutiveFailures: 3);

            manager.RecordBatchFailure();
            manager.RecordBatchFailure();
            manager.RecordBatchSuccess();

            Assert.Equal(0, manager.ConsecutiveFailures);
            Assert.False(manager.RecordBatchFailure());
        }

        [Fact]
        public void LifecycleManager_RecycleDelegatesToClient()
        {
            var client = new FakePublisherWorkerClient();
            var manager = new PublisherLifecycleManager(client);

            manager.Recycle();

            Assert.True(client.RecycleCalled);
        }

        [Fact]
        public async Task LifecycleManager_InitializeSendsHealthCheck()
        {
            var client = new FakePublisherWorkerClient();
            var manager = new PublisherLifecycleManager(client);

            await manager.InitializeAsync(CancellationToken.None);

            Assert.True(client.EnsureStartedCalled);
            Assert.NotNull(client.LastRequest);
            Assert.Equal("health", client.LastRequest!.Command);
        }

        [Fact]
        public async Task LifecycleManager_InitializeThrowsWhenHealthCheckFails()
        {
            var client = new FakePublisherWorkerClient
            {
                NextResponse = new WorkerResponse { Success = false, ErrorMessage = "boom" }
            };
            var manager = new PublisherLifecycleManager(client);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.InitializeAsync(CancellationToken.None));

            Assert.Contains("Worker initialization failed", ex.Message);
            Assert.Contains("boom", ex.Message);
        }

        [Fact]
        public async Task LifecycleManager_RenderingFailureSurfacesErrorMessage()
        {
            var client = new FakePublisherWorkerClient
            {
                NextResponse = new WorkerResponse { Success = false, ErrorMessage = "Rendering failed" }
            };
            var manager = new PublisherLifecycleManager(client);

            var record = new FileRecord { FileName = "test.pub" };

            var ex = await Assert.ThrowsAsync<RenderExportFailureException>(() =>
                manager.ExecuteRenderingJobAsync(record, "/tmp/test.pub", "/tmp/test.pdf", runLinkCheck: false, intent: RenderIntent.Commercial, docStructureTags: true, timeoutSeconds: 60, CancellationToken.None));

            Assert.Contains("Rendering failed", ex.Message);
        }

        [Fact]
        public async Task LifecycleManager_PipeBrokenSurfacesAsRenderEngineCrash()
        {
            var client = new FakePublisherWorkerClient { ThrowOnSend = true, SendException = new InvalidOperationException("Worker closed the pipe") };
            var manager = new PublisherLifecycleManager(client);

            var record = new FileRecord { FileName = "test.pub" };

            var ex = await Assert.ThrowsAsync<RenderEngineCrashException>(() =>
                manager.ExecuteRenderingJobAsync(record, "/tmp/test.pub", "/tmp/test.pdf", runLinkCheck: false, intent: RenderIntent.Commercial, docStructureTags: true, timeoutSeconds: 60, CancellationToken.None));

            Assert.Contains("terminated", ex.Message);
        }

        [Fact]
        public async Task LifecycleManager_TimeoutPropagatesUnchanged()
        {
            var client = new FakePublisherWorkerClient { ThrowOnSend = true, SendException = new TimeoutException("Worker request timed out after 5s.") };
            var manager = new PublisherLifecycleManager(client);

            var record = new FileRecord { FileName = "test.pub" };

            await Assert.ThrowsAsync<TimeoutException>(() =>
                manager.ExecuteRenderingJobAsync(record, "/tmp/test.pub", "/tmp/test.pdf", runLinkCheck: false, intent: RenderIntent.Commercial, docStructureTags: true, timeoutSeconds: 60, CancellationToken.None));
        }

        [Fact]
        public async Task LifecycleManager_RenderingSuccessCopiesMissingAssetsToRecord()
        {
            var client = new FakePublisherWorkerClient
            {
                NextResponse = new WorkerResponse
                {
                    Success = true,
                    RenderResult = new RenderResult { MissingAssetsCount = 2, MissingAssetsList = "a | b" }
                }
            };
            var manager = new PublisherLifecycleManager(client);

            var record = new FileRecord { FileName = "test.pub" };
            await manager.ExecuteRenderingJobAsync(record, "/tmp/test.pub", "/tmp/test.pdf", runLinkCheck: true, intent: RenderIntent.Commercial, docStructureTags: true, timeoutSeconds: 60, CancellationToken.None);

            Assert.Equal(2, record.MissingAssetsCount);
            Assert.Equal("a | b", record.MissingAssetsList);
            Assert.Equal("render", client.LastRequest?.Command);
            Assert.True(client.LastRequest?.RenderJob?.RunLinkCheck);
        }

        [Fact]
        public void LifecycleManager_ShutdownDisposesWorkerClient()
        {
            var client = new FakePublisherWorkerClient();
            var manager = new PublisherLifecycleManager(client);

            manager.Shutdown();

            Assert.True(client.Disposed);
            Assert.Equal("shutdown", client.LastRequest?.Command);
        }

        [Fact]
        public void LifecycleManager_ShutdownSwallowsClientExceptions()
        {
            var client = new FakePublisherWorkerClient { ThrowOnSend = true };
            var manager = new PublisherLifecycleManager(client);

            // Should not throw even though the worker fails the shutdown send.
            manager.Shutdown();

            Assert.True(client.Disposed);
        }

        [Fact]
        public void LifecycleManager_ShutdownResetsConsecutiveFailureCount()
        {
            // After a circuit-breaker trip, Shutdown is called and the failure
            // counter should clear so a follow-up "Continue" run starts fresh.
            var manager = new PublisherLifecycleManager(new FakePublisherWorkerClient(), maxConsecutiveFailures: 3);

            manager.RecordBatchFailure();
            manager.RecordBatchFailure();
            Assert.Equal(2, manager.ConsecutiveFailures);

            manager.Shutdown();

            Assert.Equal(0, manager.ConsecutiveFailures);
        }

        [Fact]
        public async Task LifecycleManager_DiCtorAfterShutdownThrowsClearErrorOnReuse()
        {
            // The DI-constructed manager has no factory to rebuild from. A
            // re-use after Shutdown must surface a clear InvalidOperationException
            // rather than an ObjectDisposedException leaking from the disposed
            // client.
            var client = new FakePublisherWorkerClient();
            var manager = new PublisherLifecycleManager(client);

            manager.Shutdown();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.ExecuteRenderingJobAsync(
                    new FileRecord { FileName = "x.pub" },
                    "/tmp/x.pub",
                    "/tmp/x.pdf",
                    runLinkCheck: false,
                    intent: RenderIntent.Commercial,
                    docStructureTags: true,
                    timeoutSeconds: 5,
                    CancellationToken.None));

            Assert.Contains("disposed", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        // -----------------------------
        // Local test doubles
        // -----------------------------

        private class HangingWorkerTransport : FakeWorkerTransport
        {
            public override async Task<WorkerResponse> ReceiveResponseAsync(CancellationToken cancellationToken)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new WorkerResponse { Success = true };
            }
        }

        private class FailingWorkerTransport : FakeWorkerTransport
        {
            public override Task<WorkerResponse> ReceiveResponseAsync(CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("Simulated transport failure");
            }
        }

        private class FakePublisherWorkerClient : IPublisherWorkerClient
        {
            public bool RecycleCalled { get; private set; }
            public bool EnsureStartedCalled { get; private set; }
            public bool Disposed { get; private set; }
            public bool ThrowOnSend { get; set; }
            public Exception? SendException { get; set; }

            public bool IsHealthy { get; set; } = true;
            public WorkerRequest? LastRequest { get; private set; }
            public WorkerResponse NextResponse { get; set; } = new WorkerResponse { Success = true };

            public Task<WorkerResponse> SendRequestAsync(WorkerRequest request, int? timeoutSeconds, CancellationToken cancellationToken)
            {
                LastRequest = request;
                if (ThrowOnSend) throw SendException ?? new InvalidOperationException("Simulated send failure");
                return Task.FromResult(NextResponse);
            }

            public Task EnsureWorkerStartedAsync(CancellationToken cancellationToken)
            {
                EnsureStartedCalled = true;
                return Task.CompletedTask;
            }

            public void RecycleWorker() => RecycleCalled = true;

            public void Dispose() => Disposed = true;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using PublisherConverter.Core;

namespace PublisherConverter.Tests
{
    /// <summary>
    /// Unit tests for worker orchestration behavior.
    /// These tests use mocks/fakes to isolate and verify orchestration logic
    /// without requiring actual Publisher COM or process launching.
    /// </summary>
    public class WorkerOrchestrationTests
    {
        [Fact]
        public async Task PublisherWorkerClient_ShouldEnsureWorkerStartedAsyncExactlyOnce()
        {
            // Arrange
            var launcher = new FakeProcessLauncher();
            var fakeTransport = new FakeWorkerTransport();
            var healthMonitor = new FakeWorkerHealthMonitor { IsHealthy = true };
            var timeoutProvider = new FakeTimeoutProvider();

            var client = new PublisherWorkerClient(launcher, () => fakeTransport, healthMonitor, timeoutProvider);

            // Act - Call multiple times
            await client.EnsureWorkerStartedAsync(CancellationToken.None);
            await client.EnsureWorkerStartedAsync(CancellationToken.None);
            await client.EnsureWorkerStartedAsync(CancellationToken.None);

            // Assert - Process should only be started once
            Assert.Single(launcher.CreatedProcesses);
            Assert.True(fakeTransport.Connected);
        }

        [Fact]
        public async Task PublisherWorkerClient_ShouldRecycleWorkerAndRestart()
        {
            // Arrange
            var launcher = new FakeProcessLauncher();
            var transports = new Queue<FakeWorkerTransport>();
            transports.Enqueue(new FakeWorkerTransport());
            transports.Enqueue(new FakeWorkerTransport());

            var healthMonitor = new FakeWorkerHealthMonitor { IsHealthy = true };
            var timeoutProvider = new FakeTimeoutProvider();

            var client = new PublisherWorkerClient(
                launcher,
                () => transports.Dequeue(),
                healthMonitor,
                timeoutProvider);

            // Act
            await client.EnsureWorkerStartedAsync(CancellationToken.None);
            Assert.Single(launcher.CreatedProcesses);

            client.RecycleWorker();

            await client.EnsureWorkerStartedAsync(CancellationToken.None);

            // Assert - New process should be started
            Assert.Equal(2, launcher.CreatedProcesses.Count);
            Assert.True(launcher.CreatedProcesses[0].Killed);
            Assert.False(launcher.CreatedProcesses[1].Killed);
        }

        [Fact]
        public async Task PublisherWorkerClient_ShouldSendRequestAndReceiveResponse()
        {
            // Arrange
            var launcher = new FakeProcessLauncher();
            var fakeTransport = new FakeWorkerTransport();
            fakeTransport.Responses.Enqueue(new WorkerResponse { Success = true });

            var healthMonitor = new FakeWorkerHealthMonitor { IsHealthy = true };
            var timeoutProvider = new FakeTimeoutProvider();

            var client = new PublisherWorkerClient(launcher, () => fakeTransport, healthMonitor, timeoutProvider);

            var request = new WorkerRequest { Command = "render", RenderJob = new RenderJob() };

            // Act
            var response = await client.SendRequestAsync(request, timeoutSeconds: 10, CancellationToken.None);

            // Assert
            Assert.NotNull(response);
            Assert.True(response.Success);
            Assert.Single(fakeTransport.SentRequests);
            Assert.Equal("render", fakeTransport.SentRequests[0].Command);
        }

        [Fact]
        public async Task PublisherWorkerClient_ShouldRecycleOnTimeout()
        {
            // Arrange
            var launcher = new FakeProcessLauncher();
            var slowTransport = new SlowWorkerTransport();
            
            var healthMonitor = new FakeWorkerHealthMonitor { IsHealthy = true };
            var timeoutProvider = new FakeTimeoutProvider { Timeout = TimeSpan.FromMilliseconds(10) };

            var client = new PublisherWorkerClient(launcher, () => slowTransport, healthMonitor, timeoutProvider);

            var request = new WorkerRequest { Command = "render" };

            // Act & Assert - Should timeout and throw
            var ex = await Assert.ThrowsAsync<TimeoutException>(
                () => client.SendRequestAsync(request, timeoutSeconds: null, CancellationToken.None));

            Assert.Contains("timed out", ex.Message);
            Assert.True(launcher.CreatedProcesses[0].Killed);
        }

        [Fact]
        public async Task PublisherWorkerClient_ShouldRecycleOnException()
        {
            // Arrange
            var launcher = new FakeProcessLauncher();
            var fakeTransport = new FailingWorkerTransport();
            
            var healthMonitor = new FakeWorkerHealthMonitor { IsHealthy = true };
            var timeoutProvider = new FakeTimeoutProvider();

            var client = new PublisherWorkerClient(launcher, () => fakeTransport, healthMonitor, timeoutProvider);

            var request = new WorkerRequest { Command = "render" };

            // Act & Assert - Should throw and recycle
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.SendRequestAsync(request, timeoutSeconds: 10, CancellationToken.None));

            Assert.True(launcher.CreatedProcesses[0].Killed);
        }

        [Fact]
        public void PublisherLifecycleManager_ShouldTrackConsecutiveFailures()
        {
            // Arrange
            var workerClient = new FakePublisherWorkerClient();
            var manager = new PublisherLifecycleManager(workerClient, maxConsecutiveFailures: 3);

            // Act
            Assert.False(manager.RecordBatchFailure()); // Count: 1
            Assert.False(manager.RecordBatchFailure()); // Count: 2
            Assert.True(manager.RecordBatchFailure());  // Count: 3, should return true

            // Assert
            Assert.Equal(3, manager.ConsecutiveFailures);
        }

        [Fact]
        public void PublisherLifecycleManager_ShouldResetConsecutiveFailuresOnSuccess()
        {
            // Arrange
            var workerClient = new FakePublisherWorkerClient();
            var manager = new PublisherLifecycleManager(workerClient, maxConsecutiveFailures: 3);

            // Act
            manager.RecordBatchFailure();
            manager.RecordBatchFailure();
            manager.RecordBatchSuccess();

            // Assert
            Assert.Equal(0, manager.ConsecutiveFailures);
            Assert.False(manager.RecordBatchFailure()); // Count: 1 again
        }

        [Fact]
        public void PublisherLifecycleManager_ShouldRecycleWorkerOnRequest()
        {
            // Arrange
            var workerClient = new FakePublisherWorkerClient();
            var manager = new PublisherLifecycleManager(workerClient, maxConsecutiveFailures: 3);

            // Act
            manager.Recycle();

            // Assert
            Assert.True(workerClient.RecycleWorkerCalled);
        }

        [Fact]
        public async Task PublisherLifecycleManager_ShouldExecuteRenderingJob()
        {
            // Arrange
            var workerClient = new FakePublisherWorkerClient();
            workerClient.SendRequestResponse = new WorkerResponse
            {
                Success = true,
                RenderResult = new RenderResult { MissingAssetsCount = 0 }
            };

            var manager = new PublisherLifecycleManager(workerClient, maxConsecutiveFailures: 3);

            var record = new FileRecord { FileName = "test.pub" };
            var sourcePath = "/tmp/test.pub";
            var targetPath = "/tmp/test.pdf";

            // Act
            await manager.ExecuteRenderingJobAsync(
                record,
                sourcePath,
                targetPath,
                runLinkCheck: false,
                timeoutSeconds: 60,
                CancellationToken.None);

            // Assert
            Assert.Equal(0, record.MissingAssetsCount);
            Assert.Equal("render", workerClient.LastSentRequest?.Command);
        }

        [Fact]
        public async Task PublisherLifecycleManager_ShouldThrowOnRenderingFailure()
        {
            // Arrange
            var workerClient = new FakePublisherWorkerClient();
            workerClient.SendRequestResponse = new WorkerResponse
            {
                Success = false,
                ErrorMessage = "Rendering failed"
            };

            var manager = new PublisherLifecycleManager(workerClient, maxConsecutiveFailures: 3);

            var record = new FileRecord { FileName = "test.pub" };

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.ExecuteRenderingJobAsync(
                    record,
                    "/tmp/test.pub",
                    "/tmp/test.pdf",
                    runLinkCheck: false,
                    timeoutSeconds: 60,
                    CancellationToken.None));

            Assert.Contains("Rendering failed", ex.Message);
        }

        /// <summary>
        /// Fake worker transport that simulates connection failures.
        /// </summary>
        private class FailingWorkerTransport : FakeWorkerTransport
        {
            public override Task<WorkerResponse> ReceiveResponseAsync(CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("Simulated transport failure");
            }
        }

        /// <summary>
        /// Fake worker transport that delays response to simulate timeout.
        /// </summary>
        private class SlowWorkerTransport : FakeWorkerTransport
        {
            public override async Task<WorkerResponse> ReceiveResponseAsync(CancellationToken cancellationToken)
            {
                await Task.Delay(5000, cancellationToken);
                return new WorkerResponse { Success = true };
            }
        }

        /// <summary>
        /// Fake worker client for testing PublisherLifecycleManager.
        /// </summary>
        private class FakePublisherWorkerClient : IPublisherWorkerClient
        {
            public bool RecycleWorkerCalled { get; set; }
            public bool IsHealthy { get; set; } = true;
            public WorkerRequest? LastSentRequest { get; set; }
            public WorkerResponse SendRequestResponse { get; set; } = new WorkerResponse { Success = true };

            public Task<WorkerResponse> SendRequestAsync(WorkerRequest request, int? timeoutSeconds, CancellationToken cancellationToken)
            {
                LastSentRequest = request;
                return Task.FromResult(SendRequestResponse);
            }

            public void EnsureWorkerStarted() { }

            public Task EnsureWorkerStartedAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public void RecycleWorker() => RecycleWorkerCalled = true;

            public void Dispose() { }
        }
    }
}

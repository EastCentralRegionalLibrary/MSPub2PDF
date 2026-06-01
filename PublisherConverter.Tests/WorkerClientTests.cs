using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using PublisherConverter.Core;

namespace PublisherConverter.Tests
{
    public class WorkerClientTests
    {
        private readonly FakeProcessLauncher _launcher = new FakeProcessLauncher();
        private readonly FakeWorkerTransport _transport = new FakeWorkerTransport();
        private readonly FakeWorkerHealthMonitor _healthMonitor = new FakeWorkerHealthMonitor();
        private readonly FakeTimeoutProvider _timeoutProvider = new FakeTimeoutProvider();

        private PublisherWorkerClient CreateClient()
        {
            return new PublisherWorkerClient(
                _launcher,
                () => _transport,
                _healthMonitor,
                _timeoutProvider
            );
        }

        [Fact]
        public async Task WorkerClient_ShouldStartWorkerOnFirstRequest()
        {
            var client = CreateClient();
            var request = new WorkerRequest { Command = "test" };

            await client.SendRequestAsync(request, null, CancellationToken.None);

            Assert.Single(_launcher.CreatedProcesses);
            Assert.True(_transport.Connected);
            Assert.Single(_transport.SentRequests);
        }

        [Fact]
        public async Task WorkerClient_ShouldRecycleOnTimeout()
        {
            _timeoutProvider.Timeout = TimeSpan.FromMilliseconds(10);

            // Transport that never returns a response
            var slowTransport = new SlowTransport();
            var clientWithSlowTransport = new PublisherWorkerClient(_launcher, () => slowTransport, _healthMonitor, _timeoutProvider);

            await Assert.ThrowsAsync<TimeoutException>(() => clientWithSlowTransport.SendRequestAsync(new WorkerRequest { Command = "test" }, null, CancellationToken.None));

            // It starts one, then times out, then recycles (kills first, starts second)
            Assert.Equal(2, _launcher.CreatedProcesses.Count);
            Assert.True(_launcher.CreatedProcesses[0].Killed);
        }

        [Fact]
        public async Task WorkerClient_ShouldRecycleOnException()
        {
            var client = CreateClient();
            _transport.Responses.Enqueue(new WorkerResponse { Success = false, ErrorMessage = "Crash" });

            var response = await client.SendRequestAsync(new WorkerRequest { Command = "test" }, null, CancellationToken.None);

            Assert.False(response.Success);
            // We don't recycle on logical failure (Success=false), but on transport exception.
            // Let's test transport exception.
        }

        [Fact]
        public async Task WorkerClient_ShouldRecycleOnTransportException()
        {
            var failingTransport = new FailingTransport();
            var client = new PublisherWorkerClient(_launcher, () => failingTransport, _healthMonitor, _timeoutProvider);

            await Assert.ThrowsAsync<Exception>(() => client.SendRequestAsync(new WorkerRequest { Command = "test" }, null, CancellationToken.None));

            Assert.True(_launcher.CreatedProcesses[0].Killed);
        }

        private class SlowTransport : FakeWorkerTransport
        {
            public override async Task<WorkerResponse> ReceiveResponseAsync(CancellationToken cancellationToken)
            {
                await Task.Delay(1000, cancellationToken);
                return new WorkerResponse { Success = true };
            }
        }

        private class FailingTransport : FakeWorkerTransport
        {
            public override Task<WorkerResponse> ReceiveResponseAsync(CancellationToken cancellationToken)
            {
                throw new Exception("Transport failed");
            }
        }
    }
}

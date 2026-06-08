using System;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core;
using PublisherConverter.Core.FontWorker;
using Xunit;

namespace PublisherConverter.Tests.FontWorker
{
    /// <summary>
    /// Covers the Linux-safe parts of the Windows privileged installer: the
    /// capability install/idempotency logic (driven by a fake PowerShell
    /// runner) and the batch dedup/isolation. The system-wide font registration
    /// path is inherently Windows-only (HKLM) and is exercised end-to-end via
    /// the loopback / worker-backed tests with a fake installer instead.
    /// </summary>
    public class WindowsPrivilegedFontInstallerTests
    {
        private sealed class FuncPowerShellRunner : IPowerShellRunner
        {
            private readonly Func<string, ProcessExecutionResult> _handler;
            public int Calls { get; private set; }

            public FuncPowerShellRunner(Func<string, ProcessExecutionResult> handler) => _handler = handler;

            public Task<ProcessExecutionResult> RunAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
            {
                Calls++;
                return Task.FromResult(_handler(script));
            }
        }

        private static WindowsPrivilegedFontInstaller Create(IPowerShellRunner runner)
            => new WindowsPrivilegedFontInstaller(runner, NullStructuredLogger.Instance, TimeSpan.FromSeconds(30), () => "/tmp/should-not-be-used");

        [Fact]
        public async Task AlreadyInstalledCapability_IsIdempotentNoOp()
        {
            var runner = new FuncPowerShellRunner(_ => new ProcessExecutionResult { ExitCode = 0, StdOut = "AlreadyInstalled" });
            var installer = Create(runner);

            var resp = await installer.InstallCapabilityAsync(new InstallCapabilityRequest { CapabilityName = "Cap.A" }, "c", CancellationToken.None);

            Assert.Equal(InstallOutcome.AlreadyPresent, resp.Outcome);
        }

        [Fact]
        public async Task FreshCapability_ReportsInstalled()
        {
            var runner = new FuncPowerShellRunner(_ => new ProcessExecutionResult { ExitCode = 0, StdOut = "AddState=Installed" });
            var installer = Create(runner);

            var resp = await installer.InstallCapabilityAsync(new InstallCapabilityRequest { CapabilityName = "Cap.A" }, "c", CancellationToken.None);

            Assert.Equal(InstallOutcome.Installed, resp.Outcome);
        }

        [Fact]
        public async Task NonZeroExit_ReportsFailed()
        {
            var runner = new FuncPowerShellRunner(_ => new ProcessExecutionResult { ExitCode = 1, StdErr = "Access denied" });
            var installer = Create(runner);

            var resp = await installer.InstallCapabilityAsync(new InstallCapabilityRequest { CapabilityName = "Cap.A" }, "c", CancellationToken.None);

            Assert.Equal(InstallOutcome.Failed, resp.Outcome);
            Assert.Contains("Access denied", resp.Detail);
        }

        [Fact]
        public async Task Timeout_ReportsFailed()
        {
            var runner = new FuncPowerShellRunner(_ => new ProcessExecutionResult { TimedOut = true, ExitCode = -1 });
            var installer = Create(runner);

            var resp = await installer.InstallCapabilityAsync(new InstallCapabilityRequest { CapabilityName = "Cap.A" }, "c", CancellationToken.None);

            Assert.Equal(InstallOutcome.Failed, resp.Outcome);
            Assert.Contains("timed out", resp.Detail);
        }

        [Fact]
        public async Task RunnerThrows_ReportsFailed_NotPropagated()
        {
            var runner = new FuncPowerShellRunner(_ => throw new InvalidOperationException("powershell missing"));
            var installer = Create(runner);

            var resp = await installer.InstallCapabilityAsync(new InstallCapabilityRequest { CapabilityName = "Cap.A" }, "c", CancellationToken.None);

            Assert.Equal(InstallOutcome.Failed, resp.Outcome);
        }

        [Fact]
        public async Task EmptyCapabilityName_FailsWithoutCallingRunner()
        {
            var runner = new FuncPowerShellRunner(_ => new ProcessExecutionResult { ExitCode = 0 });
            var installer = Create(runner);

            var resp = await installer.InstallCapabilityAsync(new InstallCapabilityRequest { CapabilityName = "  " }, "c", CancellationToken.None);

            Assert.Equal(InstallOutcome.Failed, resp.Outcome);
            Assert.Equal(0, runner.Calls);
        }

        [Fact]
        public async Task Batch_DedupesAndIsolatesPerItem()
        {
            var runner = new FuncPowerShellRunner(script =>
                script.Contains("Cap.Good")
                    ? new ProcessExecutionResult { ExitCode = 0, StdOut = "AddState=Installed" }
                    : new ProcessExecutionResult { ExitCode = 1, StdErr = "no" });
            var installer = Create(runner);

            var resp = await installer.InstallCapabilitiesBatchAsync(
                new InstallCapabilitiesBatchRequest { CapabilityNames = new[] { "Cap.Good", "Cap.Bad", "Cap.Good" } },
                "c", CancellationToken.None);

            // Deduped to two distinct capabilities, each with its own outcome.
            Assert.Equal(2, resp.Results.Count);
            Assert.Equal(2, runner.Calls);
            Assert.Contains(resp.Results, r => r.CapabilityName == "Cap.Good" && r.Outcome == InstallOutcome.Installed);
            Assert.Contains(resp.Results, r => r.CapabilityName == "Cap.Bad" && r.Outcome == InstallOutcome.Failed);
        }

        [Fact]
        public async Task InstallFont_MissingSourceFile_FailsBeforeTouchingRegistry()
        {
            var installer = Create(new FuncPowerShellRunner(_ => new ProcessExecutionResult()));

            var resp = await installer.InstallFontAsync(
                new InstallFontRequest { FamilyName = "Fam", SourcePath = "/no/such/file.ttf", FileName = "file.ttf" },
                "c", CancellationToken.None);

            Assert.Equal(InstallOutcome.Failed, resp.Outcome);
            Assert.Contains("not found", resp.Detail);
        }
    }
}

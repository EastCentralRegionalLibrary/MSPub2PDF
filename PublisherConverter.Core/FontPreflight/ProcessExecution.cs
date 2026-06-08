using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    public sealed class ProcessExecutionResult
    {
        public int ExitCode { get; init; }
        public string StdOut { get; init; } = string.Empty;
        public string StdErr { get; init; } = string.Empty;
        public bool TimedOut { get; init; }
    }

    /// <summary>
    /// Runs PowerShell commands for the Windows-capability provisioning
    /// strategy. Abstracted so tests can substitute a fake without spawning a
    /// real powershell.exe (which would also fail outside Windows).
    /// </summary>
    public interface IPowerShellRunner
    {
        Task<ProcessExecutionResult> RunAsync(string script, TimeSpan timeout, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Default runner — invokes the platform's PowerShell executable with the
    /// provided script. Stays a no-throw best-effort surface: any process
    /// startup failure is reported via the result, not exceptions.
    /// </summary>
    public sealed class DefaultPowerShellRunner : IPowerShellRunner
    {
        public async Task<ProcessExecutionResult> RunAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                return new ProcessExecutionResult { ExitCode = -1, StdErr = $"Failed to start PowerShell: {ex.Message}" };
            }
            if (process == null)
            {
                return new ProcessExecutionResult { ExitCode = -1, StdErr = "Process.Start returned null" };
            }

            try
            {
                await process.StandardInput.WriteAsync(script).ConfigureAwait(false);
                process.StandardInput.Close();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);

                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                Task waitTask = process.WaitForExitAsync(cts.Token);

                try
                {
                    await waitTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                    return new ProcessExecutionResult { ExitCode = -1, TimedOut = true, StdErr = "PowerShell invocation timed out." };
                }

                string stdout = await stdoutTask.ConfigureAwait(false);
                string stderr = await stderrTask.ConfigureAwait(false);
                return new ProcessExecutionResult
                {
                    ExitCode = process.ExitCode,
                    StdOut = stdout ?? string.Empty,
                    StdErr = stderr ?? string.Empty,
                };
            }
            catch (Exception ex)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                return new ProcessExecutionResult { ExitCode = -1, StdErr = $"PowerShell invocation faulted: {ex.Message}" };
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}

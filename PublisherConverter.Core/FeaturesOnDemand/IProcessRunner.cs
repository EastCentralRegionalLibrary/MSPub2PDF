using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FeaturesOnDemand
{
    /// <summary>
    /// Runs an external executable and captures its result. Abstracted so the
    /// Windows <c>expand.exe</c> extractor can be unit-tested without spawning a
    /// real process (and without requiring Windows). Mirrors the no-throw,
    /// result-carrying contract of <see cref="IPowerShellRunner"/>.
    /// </summary>
    public interface IProcessRunner
    {
        Task<ProcessExecutionResult> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken);
    }

    /// <summary>Default runner that launches the executable via <see cref="Process"/>.</summary>
    public sealed class DefaultProcessRunner : IProcessRunner
    {
        public async Task<ProcessExecutionResult> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
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
                return new ProcessExecutionResult { ExitCode = -1, StdErr = $"Failed to start {fileName}: {ex.Message}" };
            }
            if (process == null)
            {
                return new ProcessExecutionResult { ExitCode = -1, StdErr = "Process.Start returned null" };
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);

                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                try
                {
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                    return new ProcessExecutionResult { ExitCode = -1, TimedOut = true, StdErr = $"{fileName} timed out." };
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
                return new ProcessExecutionResult { ExitCode = -1, StdErr = $"{fileName} invocation faulted: {ex.Message}" };
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Resolves missing fonts that map to a Windows optional-font capability
    /// (e.g. "Language.Fonts.Hant~~~und-HANT0.0.1.0" → Traditional Chinese
    /// PMingLiU / MingLiU). Idempotent — Get-WindowsCapability is consulted
    /// first; Add-WindowsCapability is only invoked when not already installed.
    ///
    /// Best-effort: any failure (no elevation, offline, unsupported edition,
    /// non-Windows, PowerShell missing) is reported via the log lines and
    /// returns false so the FontResolver can fall through to the next
    /// strategy (typically the downloadable fallback).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsCapabilityProvisioningStrategy : IFontProvisioningStrategy
    {
        private readonly FontMappingTable _mappings;
        private readonly IPowerShellRunner _runner;
        private readonly TimeSpan _timeout;

        public WindowsCapabilityProvisioningStrategy(FontMappingTable mappings, IPowerShellRunner runner)
            : this(mappings, runner, TimeSpan.FromMinutes(3))
        {
        }

        public WindowsCapabilityProvisioningStrategy(FontMappingTable mappings, IPowerShellRunner runner, TimeSpan timeout)
        {
            _mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _timeout = timeout;
        }

        public string Name => "WindowsCapability";

        public bool CanResolve(string fontFamily) => _mappings.TryGetCapability(fontFamily, out _);

        public async Task<bool> TryResolveAsync(string fontFamily, IList<string> log, CancellationToken cancellationToken)
        {
            if (!_mappings.TryGetCapability(fontFamily, out var capabilityName)) return false;

            // Idempotent script: check first, install only if needed.
            string escaped = capabilityName.Replace("'", "''");
            string script =
                $"$ErrorActionPreference = 'Stop';\n" +
                $"$c = Get-WindowsCapability -Online -Name '{escaped}' -ErrorAction SilentlyContinue;\n" +
                $"if ($c -and $c.State -eq 'Installed') {{ Write-Output 'AlreadyInstalled'; exit 0 }};\n" +
                $"$r = Add-WindowsCapability -Online -Name '{escaped}';\n" +
                $"Write-Output ('AddState=' + $r.State);\n" +
                $"if ($r.State -eq 'Installed' -or $r.RestartNeeded -eq $true) {{ exit 0 }} else {{ exit 1 }}";

            log.Add($"    capability={capabilityName}");
            ProcessExecutionResult result;
            try
            {
                result = await _runner.RunAsync(script, _timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.Add($"    ! capability runner threw: {ex.Message}");
                return false;
            }

            if (result.TimedOut)
            {
                log.Add($"    ! capability install timed out after {_timeout.TotalSeconds:F0}s.");
                return false;
            }

            if (result.ExitCode == 0)
            {
                string stdout = (result.StdOut ?? string.Empty).Trim();
                if (stdout.Contains("AlreadyInstalled", StringComparison.Ordinal))
                {
                    log.Add($"    capability already installed.");
                }
                else if (!string.IsNullOrEmpty(stdout))
                {
                    log.Add($"    {stdout}");
                }
                return true;
            }

            string err = (result.StdErr ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(err)) err = (result.StdOut ?? string.Empty).Trim();
            log.Add($"    ! capability install failed (exit {result.ExitCode}): {Truncate(err, 240)}");
            return false;
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}

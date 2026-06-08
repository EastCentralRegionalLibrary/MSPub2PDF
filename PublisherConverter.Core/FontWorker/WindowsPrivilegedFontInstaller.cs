using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PublisherConverter.Core.FontWorker
{
    /// <summary>
    /// Windows implementation of <see cref="IPrivilegedFontInstaller"/>. Runs
    /// only inside the elevated worker process.
    ///
    /// * Capabilities are installed with Add-WindowsCapability (via the shared
    ///   <see cref="IPowerShellRunner"/>), checking Get-WindowsCapability first
    ///   so an already-installed capability is a no-op.
    /// * Fonts are installed system-wide by copying into %WINDIR%\Fonts and
    ///   registering the family under the HKLM Fonts key. An identical existing
    ///   registration is a no-op.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsPrivilegedFontInstaller : IPrivilegedFontInstaller
    {
        private const string HklmFontsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

        private readonly IPowerShellRunner _runner;
        private readonly IStructuredLogger _logger;
        private readonly TimeSpan _capabilityTimeout;
        private readonly Func<string> _systemFontDirectoryProvider;

        public WindowsPrivilegedFontInstaller(IPowerShellRunner runner, IStructuredLogger? logger = null)
            : this(runner, logger, TimeSpan.FromMinutes(5), DefaultSystemFontDirectory)
        {
        }

        public WindowsPrivilegedFontInstaller(
            IPowerShellRunner runner,
            IStructuredLogger? logger,
            TimeSpan capabilityTimeout,
            Func<string> systemFontDirectoryProvider)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _logger = logger ?? NullStructuredLogger.Instance;
            _capabilityTimeout = capabilityTimeout;
            _systemFontDirectoryProvider = systemFontDirectoryProvider ?? DefaultSystemFontDirectory;
        }

        public async Task<InstallCapabilityResponse> InstallCapabilityAsync(InstallCapabilityRequest request, string correlationId, CancellationToken cancellationToken)
        {
            string capabilityName = request.CapabilityName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(capabilityName))
            {
                return InstallCapabilityResponse.Failed(capabilityName, "Capability name was empty.");
            }

            // Idempotent script: check state first, only Add when not installed.
            string escaped = capabilityName.Replace("'", "''");
            string script =
                $"$ErrorActionPreference = 'Stop';\n" +
                $"$c = Get-WindowsCapability -Online -Name '{escaped}' -ErrorAction SilentlyContinue;\n" +
                $"if ($c -and $c.State -eq 'Installed') {{ Write-Output 'AlreadyInstalled'; exit 0 }};\n" +
                $"$r = Add-WindowsCapability -Online -Name '{escaped}';\n" +
                $"Write-Output ('AddState=' + $r.State);\n" +
                $"if ($r.State -eq 'Installed' -or $r.RestartNeeded -eq $true) {{ exit 0 }} else {{ exit 1 }}";

            ProcessExecutionResult result;
            try
            {
                result = await _runner.RunAsync(script, _capabilityTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("capability.install.error", correlationId, Fields(("capability", capabilityName), ("error", ex.Message)));
                return InstallCapabilityResponse.Failed(capabilityName, $"Runner threw: {ex.Message}");
            }

            if (result.TimedOut)
            {
                _logger.Error("capability.install.timeout", correlationId, Fields(("capability", capabilityName), ("timeoutSeconds", _capabilityTimeout.TotalSeconds)));
                return InstallCapabilityResponse.Failed(capabilityName, $"Capability install timed out after {_capabilityTimeout.TotalSeconds:F0}s.");
            }

            if (result.ExitCode == 0)
            {
                string stdout = (result.StdOut ?? string.Empty).Trim();
                bool already = stdout.Contains("AlreadyInstalled", StringComparison.Ordinal);
                var outcome = already ? InstallOutcome.AlreadyPresent : InstallOutcome.Installed;
                _logger.Info("capability.install.ok", correlationId, Fields(("capability", capabilityName), ("outcome", outcome.ToString())));
                return new InstallCapabilityResponse
                {
                    CapabilityName = capabilityName,
                    Outcome = outcome,
                    Detail = already ? "Already installed." : (string.IsNullOrEmpty(stdout) ? "Installed." : stdout),
                };
            }

            string err = (result.StdErr ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(err)) err = (result.StdOut ?? string.Empty).Trim();
            _logger.Error("capability.install.failed", correlationId, Fields(("capability", capabilityName), ("exitCode", result.ExitCode), ("detail", Truncate(err, 240))));
            return InstallCapabilityResponse.Failed(capabilityName, $"Exit {result.ExitCode}: {Truncate(err, 240)}");
        }

        public async Task<InstallCapabilitiesBatchResponse> InstallCapabilitiesBatchAsync(InstallCapabilitiesBatchRequest request, string correlationId, CancellationToken cancellationToken)
        {
            var results = new List<InstallCapabilityResponse>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in request.CapabilityNames ?? Array.Empty<string>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;

                // Per-item failure isolation: one capability that fails to
                // install does not abort the rest of the batch.
                results.Add(await InstallCapabilityAsync(new InstallCapabilityRequest { CapabilityName = name }, correlationId, cancellationToken).ConfigureAwait(false));
            }

            return new InstallCapabilitiesBatchResponse { Results = results };
        }

        public Task<InstallFontResponse> InstallFontAsync(InstallFontRequest request, string correlationId, CancellationToken cancellationToken)
        {
            string family = request.FamilyName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(request.SourcePath) || !File.Exists(request.SourcePath))
            {
                return Task.FromResult(InstallFontResponse.Failed(family, $"Source font file not found: {request.SourcePath}"));
            }

            string fileName = SafeFileName(!string.IsNullOrWhiteSpace(request.FileName) ? request.FileName : Path.GetFileName(request.SourcePath));

            string targetPath;
            try
            {
                string dir = _systemFontDirectoryProvider();
                Directory.CreateDirectory(dir);
                targetPath = Path.Combine(dir, fileName);
            }
            catch (Exception ex)
            {
                return Task.FromResult(InstallFontResponse.Failed(family, $"Cannot access system font directory: {ex.Message}"));
            }

            bool fileAlreadyPresent = File.Exists(targetPath);
            if (!fileAlreadyPresent)
            {
                try
                {
                    string tempPath = targetPath + ".part";
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    File.Copy(request.SourcePath, tempPath, overwrite: true);
                    File.Move(tempPath, targetPath);
                }
                catch (Exception ex)
                {
                    _logger.Error("font.install.copy_failed", correlationId, Fields(("family", family), ("target", targetPath), ("error", ex.Message)));
                    return Task.FromResult(InstallFontResponse.Failed(family, $"Copy to system fonts failed: {ex.Message}"));
                }
            }

            var (registered, alreadyRegistered, detail) = RegisterSystemFont(family, targetPath);
            if (!registered)
            {
                _logger.Error("font.install.register_failed", correlationId, Fields(("family", family), ("detail", detail)));
                return Task.FromResult(InstallFontResponse.Failed(family, detail));
            }

            var outcome = (fileAlreadyPresent && alreadyRegistered) ? InstallOutcome.AlreadyPresent : InstallOutcome.Installed;
            _logger.Info("font.install.ok", correlationId, Fields(("family", family), ("target", targetPath), ("outcome", outcome.ToString())));
            return Task.FromResult(new InstallFontResponse { FamilyName = family, Outcome = outcome, Detail = detail });
        }

        private static (bool registered, bool alreadyRegistered, string detail) RegisterSystemFont(string family, string fullPath)
        {
            try
            {
                string suffix = string.Equals(Path.GetExtension(fullPath), ".otf", StringComparison.OrdinalIgnoreCase)
                    ? "OpenType" : "TrueType";
                string valueName = $"{family} ({suffix})";

                using var key = Registry.LocalMachine.CreateSubKey(HklmFontsKey, writable: true);
                if (key == null)
                {
                    return (false, false, $"Could not open HKLM\\{HklmFontsKey}.");
                }

                // System fonts are typically registered by bare file name (they
                // live in the system fonts directory) rather than full path.
                string registeredValue = Path.GetFileName(fullPath);
                var existing = key.GetValue(valueName) as string;
                if (string.Equals(existing, registeredValue, StringComparison.OrdinalIgnoreCase))
                {
                    return (true, true, "System font registration already up-to-date.");
                }

                key.SetValue(valueName, registeredValue, RegistryValueKind.String);
                return (true, false, $"Registered system font \"{valueName}\".");
            }
            catch (Exception ex)
            {
                return (false, false, $"HKLM registration failed: {ex.Message}");
            }
        }

        private static string DefaultSystemFontDirectory()
        {
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return Path.Combine(windir, "Fonts");
        }

        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = new char[name.Length];
            for (int i = 0; i < name.Length; i++)
            {
                chars[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
            }
            return new string(chars);
        }

        private static IReadOnlyDictionary<string, object?> Fields(params (string Key, object? Value)[] pairs)
        {
            var d = new Dictionary<string, object?>(pairs.Length);
            foreach (var (k, v) in pairs) d[k] = v;
            return d;
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}

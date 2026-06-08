using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Saves a font payload into the current-user font directory and
    /// registers it under HKCU. Shared by every "download then register"
    /// strategy (Google Fonts, GitHub, direct-URL fallback) so the
    /// install location and HKCU bookkeeping live in exactly one place.
    ///
    /// Idempotency:
    ///   * If the target file is already on disk, downloads are skipped.
    ///   * If the HKCU value already points at the same path, the write
    ///     is a no-op.
    /// </summary>
    public interface IUserFontInstaller
    {
        /// <summary>
        /// Copies the bytes from <paramref name="source"/> into the user font
        /// directory under <paramref name="desiredFileName"/> and registers
        /// <paramref name="familyName"/> in HKCU. Used by archive extraction
        /// (e.g. unzipping a GitHub release).
        /// </summary>
        Task<bool> InstallFromStreamAsync(string familyName, string desiredFileName, Stream source, IList<string> log, CancellationToken cancellationToken);

        /// <summary>
        /// Downloads the font at <paramref name="url"/> (idempotent — skipped
        /// if the file is already present) and registers it for
        /// <paramref name="familyName"/>.
        /// </summary>
        Task<bool> InstallFromUrlAsync(string familyName, string url, string? suggestedFileName, IList<string> log, CancellationToken cancellationToken);
    }

    [SupportedOSPlatform("windows")]
    public sealed class WindowsUserFontInstaller : IUserFontInstaller
    {
        private const string HkcuFontsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

        private readonly IFontDownloader _downloader;
        private readonly Func<string> _userFontDirectoryProvider;

        public WindowsUserFontInstaller(IFontDownloader downloader)
            : this(downloader, DefaultUserFontDirectory)
        {
        }

        public WindowsUserFontInstaller(IFontDownloader downloader, Func<string> userFontDirectoryProvider)
        {
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _userFontDirectoryProvider = userFontDirectoryProvider ?? DefaultUserFontDirectory;
        }

        public async Task<bool> InstallFromStreamAsync(string familyName, string desiredFileName, Stream source, IList<string> log, CancellationToken cancellationToken)
        {
            if (!TryPrepareTargetPath(desiredFileName, log, out string targetPath)) return false;

            if (!File.Exists(targetPath))
            {
                string tempPath = targetPath + ".part";
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    using (var dst = File.Create(tempPath))
                    {
                        await source.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
                    }
                    File.Move(tempPath, targetPath);
                    log.Add($"      saved → {targetPath}");
                }
                catch (OperationCanceledException)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    throw;
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    log.Add($"      ! save failed: {ex.Message}");
                    return false;
                }
            }
            else
            {
                log.Add($"      {Path.GetFileName(targetPath)} already present.");
            }

            return TryRegisterUserFont(familyName, targetPath, log);
        }

        public async Task<bool> InstallFromUrlAsync(string familyName, string url, string? suggestedFileName, IList<string> log, CancellationToken cancellationToken)
        {
            string fileName = SafeFileName(
                !string.IsNullOrWhiteSpace(suggestedFileName)
                    ? suggestedFileName!
                    : DeriveFileNameFromUrl(url) ?? (familyName + ".ttf"));

            if (!TryPrepareTargetPath(fileName, log, out string targetPath)) return false;

            if (!File.Exists(targetPath))
            {
                string tempPath = targetPath + ".part";
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    using (var dst = File.Create(tempPath))
                    {
                        await _downloader.DownloadAsync(url, dst, cancellationToken).ConfigureAwait(false);
                    }
                    File.Move(tempPath, targetPath);
                    log.Add($"      downloaded → {targetPath}");
                }
                catch (OperationCanceledException)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    throw;
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    log.Add($"      ! download failed: {ex.Message}");
                    return false;
                }
            }
            else
            {
                log.Add($"      {fileName} already present; skipping download.");
            }

            return TryRegisterUserFont(familyName, targetPath, log);
        }

        private bool TryPrepareTargetPath(string desiredFileName, IList<string> log, out string targetPath)
        {
            targetPath = string.Empty;
            try
            {
                string dir = _userFontDirectoryProvider();
                Directory.CreateDirectory(dir);
                targetPath = Path.Combine(dir, SafeFileName(desiredFileName));
                return true;
            }
            catch (Exception ex)
            {
                log.Add($"      ! cannot prepare user font directory: {ex.Message}");
                return false;
            }
        }

        private static bool TryRegisterUserFont(string family, string fullPath, IList<string> log)
        {
            try
            {
                string suffix = string.Equals(Path.GetExtension(fullPath), ".otf", StringComparison.OrdinalIgnoreCase)
                    ? "OpenType" : "TrueType";
                string valueName = $"{family} ({suffix})";

                using var key = Registry.CurrentUser.CreateSubKey(HkcuFontsKey, writable: true);
                if (key == null)
                {
                    log.Add($"      ! could not open HKCU\\{HkcuFontsKey}.");
                    return false;
                }

                var existing = key.GetValue(valueName) as string;
                if (string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    log.Add($"      HKCU font registration already up-to-date.");
                    return true;
                }

                key.SetValue(valueName, fullPath, RegistryValueKind.String);
                log.Add($"      registered HKCU font \"{valueName}\".");
                return true;
            }
            catch (Exception ex)
            {
                log.Add($"      ! HKCU registration failed: {ex.Message}");
                return false;
            }
        }

        private static string DefaultUserFontDirectory()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "Microsoft", "Windows", "Fonts");
        }

        private static string? DeriveFileNameFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            try
            {
                var uri = new Uri(url);
                string name = Path.GetFileName(uri.LocalPath);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch
            {
                return null;
            }
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
    }
}

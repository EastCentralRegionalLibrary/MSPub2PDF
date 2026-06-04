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
    /// Resolves missing fonts via a downloadable .ttf/.otf into the
    /// current-user font directory (%LocalAppData%\Microsoft\Windows\Fonts)
    /// with a matching HKCU registration. No admin elevation required.
    ///
    /// Idempotency:
    ///   * If the target font file already exists, the download is skipped.
    ///   * The HKCU "Fonts" value name is checked before being written; the
    ///     write is a no-op when an existing value points at the same path.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class DownloadableFontProvisioningStrategy : IFontProvisioningStrategy
    {
        private const string HkcuFontsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

        private readonly FontMappingTable _mappings;
        private readonly IFontDownloader _downloader;
        private readonly Func<string> _userFontDirectoryProvider;

        public DownloadableFontProvisioningStrategy(FontMappingTable mappings, IFontDownloader downloader)
            : this(mappings, downloader, DefaultUserFontDirectory)
        {
        }

        public DownloadableFontProvisioningStrategy(
            FontMappingTable mappings,
            IFontDownloader downloader,
            Func<string> userFontDirectoryProvider)
        {
            _mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _userFontDirectoryProvider = userFontDirectoryProvider ?? DefaultUserFontDirectory;
        }

        public string Name => "DownloadableFallback";

        public bool CanResolve(string fontFamily) => _mappings.TryGetFallback(fontFamily, out _);

        public async Task<bool> TryResolveAsync(string fontFamily, IList<string> log, CancellationToken cancellationToken)
        {
            if (!_mappings.TryGetFallback(fontFamily, out var fallback)) return false;

            log.Add($"    fallback={fallback.Name} url={fallback.Url}");

            string targetDir;
            try
            {
                targetDir = _userFontDirectoryProvider();
                Directory.CreateDirectory(targetDir);
            }
            catch (Exception ex)
            {
                log.Add($"    ! cannot prepare user font directory: {ex.Message}");
                return false;
            }

            string fileName = SafeFileName(
                !string.IsNullOrWhiteSpace(fallback.FileName)
                    ? fallback.FileName!
                    : DeriveFileNameFromUrl(fallback.Url) ?? (fallback.Name + ".ttf"));
            string targetPath = Path.Combine(targetDir, fileName);

            if (!File.Exists(targetPath))
            {
                string tempPath = targetPath + ".part";
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                    using (var dst = File.Create(tempPath))
                    {
                        await _downloader.DownloadAsync(fallback.Url, dst, cancellationToken).ConfigureAwait(false);
                    }
                    File.Move(tempPath, targetPath);
                    log.Add($"    downloaded → {targetPath}");
                }
                catch (OperationCanceledException)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    throw;
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    log.Add($"    ! download failed: {ex.Message}");
                    return false;
                }
            }
            else
            {
                log.Add($"    {fileName} already present; skipping download.");
            }

            if (!TryRegisterUserFont(fallback.Name, targetPath, log))
            {
                return false;
            }

            return true;
        }

        // ---- helpers ----

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
                    log.Add($"    ! could not open HKCU\\{HkcuFontsKey}.");
                    return false;
                }

                var existing = key.GetValue(valueName) as string;
                if (string.Equals(existing, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    log.Add($"    HKCU font registration already up-to-date.");
                    return true;
                }

                key.SetValue(valueName, fullPath, RegistryValueKind.String);
                log.Add($"    registered HKCU font \"{valueName}\".");
                return true;
            }
            catch (Exception ex)
            {
                log.Add($"    ! HKCU registration failed: {ex.Message}");
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

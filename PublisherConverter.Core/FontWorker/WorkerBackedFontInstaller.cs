using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FontWorker
{
    /// <summary>
    /// An <see cref="IUserFontInstaller"/> that installs fonts system-wide
    /// through the elevated worker. The (non-privileged) network download and
    /// temp staging happen here in the main process; only the final, privileged
    /// "place into the system fonts directory + register HKLM" step is routed
    /// to the worker via an <see cref="InstallFontRequest"/>.
    ///
    /// Because it implements the same interface as the user-level
    /// <see cref="WindowsUserFontInstaller"/>, the existing download strategies
    /// (Google Fonts, GitHub, direct URL) work unchanged — the coordinator
    /// simply hands them this installer when elevated installs are in effect.
    /// </summary>
    public sealed class WorkerBackedFontInstaller : IUserFontInstaller
    {
        private readonly IElevatedFontWorkerClient _workerClient;
        private readonly IFontDownloader _downloader;
        private readonly Func<string> _stagingDirectoryProvider;
        private readonly int? _requestTimeoutSeconds;

        public WorkerBackedFontInstaller(
            IElevatedFontWorkerClient workerClient,
            IFontDownloader downloader,
            Func<string>? stagingDirectoryProvider = null,
            int? requestTimeoutSeconds = null)
        {
            _workerClient = workerClient ?? throw new ArgumentNullException(nameof(workerClient));
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _stagingDirectoryProvider = stagingDirectoryProvider ?? DefaultStagingDirectory;
            _requestTimeoutSeconds = requestTimeoutSeconds;
        }

        public async Task<bool> InstallFromStreamAsync(string familyName, string desiredFileName, Stream source, IList<string> log, CancellationToken cancellationToken)
        {
            string fileName = SafeFileName(desiredFileName);
            if (!TryStage(fileName, log, out string stagedPath)) return false;

            try
            {
                using (var dst = File.Create(stagedPath))
                {
                    await source.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
                }
                return await SendToWorkerAsync(familyName, stagedPath, fileName, log, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.Add($"      ! staging failed: {ex.Message}");
                return false;
            }
            finally
            {
                TryDelete(stagedPath);
            }
        }

        public async Task<bool> InstallFromUrlAsync(string familyName, string url, string? suggestedFileName, IList<string> log, CancellationToken cancellationToken)
        {
            string fileName = SafeFileName(
                !string.IsNullOrWhiteSpace(suggestedFileName)
                    ? suggestedFileName!
                    : DeriveFileNameFromUrl(url) ?? (familyName + ".ttf"));

            if (!TryStage(fileName, log, out string stagedPath)) return false;

            try
            {
                using (var dst = File.Create(stagedPath))
                {
                    await _downloader.DownloadAsync(url, dst, cancellationToken).ConfigureAwait(false);
                }
                return await SendToWorkerAsync(familyName, stagedPath, fileName, log, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.Add($"      ! download failed: {ex.Message}");
                return false;
            }
            finally
            {
                TryDelete(stagedPath);
            }
        }

        private async Task<bool> SendToWorkerAsync(string familyName, string stagedPath, string fileName, IList<string> log, CancellationToken cancellationToken)
        {
            string correlationId = FontWorkerProtocol.NewCorrelationId();
            var response = await _workerClient.InstallFontAsync(
                new InstallFontRequest { FamilyName = familyName, SourcePath = stagedPath, FileName = fileName },
                correlationId,
                _requestTimeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            if (response.Outcome.IsSuccess())
            {
                log.Add($"      system-wide install via worker: {response.Outcome} ({fileName}).");
                return true;
            }

            log.Add($"      ! worker system-install failed: {response.Detail}");
            return false;
        }

        private bool TryStage(string fileName, IList<string> log, out string stagedPath)
        {
            stagedPath = string.Empty;
            try
            {
                string dir = _stagingDirectoryProvider();
                Directory.CreateDirectory(dir);
                stagedPath = Path.Combine(dir, $"{Guid.NewGuid():N}_{fileName}");
                return true;
            }
            catch (Exception ex)
            {
                log.Add($"      ! cannot prepare staging directory: {ex.Message}");
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string DefaultStagingDirectory()
            => Path.Combine(Path.GetTempPath(), "MSPub2PDF", "font-staging");

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
            if (string.IsNullOrEmpty(name)) return "font.ttf";
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

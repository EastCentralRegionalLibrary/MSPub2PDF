using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FontWorker;

namespace PublisherConverter.Core.FeaturesOnDemand
{
    /// <summary>
    /// Batched Features-on-Demand font fallback. Stages the whole set of missing
    /// fonts through map → resolve → download → verify/extract → install instead
    /// of looping per font. Concerns stay separated behind seams: the
    /// <see cref="IUupDumpClient"/> finds packages, the <see cref="IFontDownloader"/>
    /// fetches CABs, the <see cref="ICabSignatureVerifier"/> gates trust, the
    /// <see cref="ICabFontExtractor"/> pulls out fonts, and the
    /// <see cref="IUserFontInstaller"/> installs them. Every per-language step is
    /// isolated so one bad package never aborts the batch, and a single
    /// correlation id (with per-language suffixes) threads the run end-to-end.
    /// </summary>
    public sealed class FeaturesOnDemandFontPipeline : IFeaturesOnDemandFontPipeline
    {
        private readonly FontLanguageResolver _languageResolver;
        private readonly IUupDumpClient _client;
        private readonly IFontDownloader _downloader;
        private readonly ICabSignatureVerifier _verifier;
        private readonly ICabFontExtractor _extractor;
        private readonly IStructuredLogger _logger;

        public FeaturesOnDemandFontPipeline(
            FontLanguageResolver languageResolver,
            IUupDumpClient client,
            IFontDownloader downloader,
            ICabSignatureVerifier verifier,
            ICabFontExtractor extractor,
            IStructuredLogger? logger = null)
        {
            _languageResolver = languageResolver ?? throw new ArgumentNullException(nameof(languageResolver));
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
            _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
            _logger = logger ?? NullStructuredLogger.Instance;
        }

        public async Task<FoDPipelineResult> RunAsync(
            IReadOnlyList<string> missingFonts,
            IUserFontInstaller installer,
            FeaturesOnDemandOptions options,
            string? correlationId,
            IProgress<FoDProgress>? progress,
            CancellationToken cancellationToken)
        {
            if (installer == null) throw new ArgumentNullException(nameof(installer));
            options ??= FeaturesOnDemandOptions.Default;
            string corr = string.IsNullOrEmpty(correlationId) ? FontWorkerProtocol.NewCorrelationId() : correlationId!;
            var input = missingFonts ?? (IReadOnlyList<string>)Array.Empty<string>();
            var log = new List<string>();

            // Stage 1 — map all missing fonts to language tokens.
            var byLanguage = _languageResolver.GroupByLanguage(input);
            Report(progress, FoDStage.Map, $"Mapped {input.Count} font(s) to {byLanguage.Count} language(s).", 0, byLanguage.Count, corr);
            log.Add($"FoD: mapped {input.Count} missing font(s) to {byLanguage.Count} language package(s).");
            if (byLanguage.Count == 0)
            {
                return AllMissing(input, log);
            }

            // Stage 2 — resolve packages for every language. The update id and file
            // manifest are fetched once and shared (every language lives in the
            // same update), then each language's CAB is selected from that manifest.
            IReadOnlyList<ResolvedFontPackage> packages;
            try
            {
                packages = await ResolvePackagesAsync(byLanguage.Keys, options, corr, progress, log, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.Add($"FoD: package resolution failed ({ex.Message}); nothing installed.");
                _logger.Error("fod.resolve.failed", corr, Fields(("error", ex.Message)));
                return AllMissing(input, log);
            }

            if (packages.Count == 0)
            {
                log.Add("FoD: no matching LanguageFeatures font packages were found for the required languages.");
                return AllMissing(input, log);
            }

            string runRoot = Path.Combine(options.ResolveScratchRoot(), $"run-{SafeFileName(corr)}");
            string downloadDir = Path.Combine(runRoot, "cab");
            string extractDir = Path.Combine(runRoot, "fonts");
            string quarantineDir = Path.Combine(runRoot, "quarantine");

            var quarantined = new List<string>();
            try
            {
                Directory.CreateDirectory(downloadDir);

                // Stage 3 — download all required CABs concurrently.
                var downloaded = await DownloadPackagesAsync(packages, downloadDir, options, corr, progress, log, cancellationToken).ConfigureAwait(false);

                // Stage 4 — verify signatures and extract fonts (per-CAB isolation,
                // reject + quarantine anything that fails verification).
                var extractedByLanguage = await VerifyAndExtractAsync(downloaded, extractDir, quarantineDir, options, corr, progress, log, quarantined, cancellationToken).ConfigureAwait(false);

                // Stage 5 — install every extracted font in one final batch.
                var resolved = await InstallAsync(extractedByLanguage, byLanguage, installer, corr, progress, log, cancellationToken).ConfigureAwait(false);

                var stillMissing = input.Where(f => !resolved.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();
                _logger.Info("fod.complete", corr, Fields(
                    ("missing", input.Count),
                    ("resolved", resolved.Count),
                    ("stillMissing", stillMissing.Count),
                    ("quarantined", quarantined.Count)));
                log.Add($"FoD: resolved {resolved.Count}, still missing {stillMissing.Count}, quarantined {quarantined.Count} CAB(s).");

                return new FoDPipelineResult
                {
                    Resolved = resolved.ToList(),
                    StillMissing = stillMissing,
                    Log = log,
                    Quarantined = quarantined,
                };
            }
            finally
            {
                // Keep quarantined CABs for inspection; clean the rest. The run
                // root is removed only when it is empty (i.e. nothing quarantined).
                TryDelete(downloadDir);
                TryDelete(extractDir);
                TryDeleteIfEmpty(runRoot);
            }
        }

        // ---- Stage 2 ----------------------------------------------------------

        private async Task<IReadOnlyList<ResolvedFontPackage>> ResolvePackagesAsync(
            IEnumerable<string> languages,
            FeaturesOnDemandOptions options,
            string corr,
            IProgress<FoDProgress>? progress,
            List<string> log,
            CancellationToken cancellationToken)
        {
            string updateId = await _client.FindLatestUpdateIdAsync(options.BuildSearch, options.Architecture, corr, cancellationToken).ConfigureAwait(false);
            log.Add($"FoD: resolved update {updateId} for build '{options.BuildSearch}' ({options.Architecture}).");
            var files = await _client.GetFilesAsync(updateId, corr, cancellationToken).ConfigureAwait(false);

            var languageList = languages.ToList();
            var resolved = new List<ResolvedFontPackage>();
            int done = 0;
            foreach (var language in languageList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                done++;
                if (UupDumpClient.TrySelectFontPackage(files, language, options.Architecture, updateId, out var package))
                {
                    resolved.Add(package);
                    log.Add($"  • {language}: selected {package}.");
                }
                else
                {
                    log.Add($"  • {language}: no matching {options.Architecture} font package found; skipping.");
                }
                Report(progress, FoDStage.Resolve, $"Resolved {language}.", done, languageList.Count, corr);
            }
            return resolved;
        }

        // ---- Stage 3 ----------------------------------------------------------

        private async Task<IReadOnlyList<(ResolvedFontPackage Package, string CabPath)>> DownloadPackagesAsync(
            IReadOnlyList<ResolvedFontPackage> packages,
            string downloadDir,
            FeaturesOnDemandOptions options,
            string corr,
            IProgress<FoDProgress>? progress,
            List<string> log,
            CancellationToken cancellationToken)
        {
            var results = new ConcurrentBag<(ResolvedFontPackage, string)>();
            int completed = 0;
            using var gate = new SemaphoreSlim(options.SafeDownloadConcurrency);

            var tasks = packages.Select(async package =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    string cabPath = Path.Combine(downloadDir, SafeFileName(package.FileName));
                    string partPath = cabPath + ".part";
                    using (var dst = File.Create(partPath))
                    {
                        await _downloader.DownloadAsync(package.DownloadUrl, dst, cancellationToken).ConfigureAwait(false);
                    }
                    if (File.Exists(cabPath)) File.Delete(cabPath);
                    File.Move(partPath, cabPath);
                    results.Add((package, cabPath));
                    LogLine(log, $"  • downloaded {package.FileName} ({package.Language}).");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Per-package isolation — one failed download does not stop the batch.
                    LogLine(log, $"  ! download failed for {package.Language} ({package.FileName}): {ex.Message}");
                    _logger.Error("fod.download.failed", $"{corr}:{package.Language}", Fields(("file", package.FileName), ("error", ex.Message)));
                }
                finally
                {
                    int n = Interlocked.Increment(ref completed);
                    Report(progress, FoDStage.Download, $"Downloaded {package.FileName}.", n, packages.Count, corr);
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return results.ToList();
        }

        // ---- Stage 4 ----------------------------------------------------------

        private async Task<IReadOnlyDictionary<string, List<ExtractedFont>>> VerifyAndExtractAsync(
            IReadOnlyList<(ResolvedFontPackage Package, string CabPath)> downloaded,
            string extractDir,
            string quarantineDir,
            FeaturesOnDemandOptions options,
            string corr,
            IProgress<FoDProgress>? progress,
            List<string> log,
            List<string> quarantined,
            CancellationToken cancellationToken)
        {
            var perLanguage = new ConcurrentDictionary<string, List<ExtractedFont>>(StringComparer.OrdinalIgnoreCase);
            int completed = 0;
            using var gate = new SemaphoreSlim(options.SafeExtractConcurrency);

            var tasks = downloaded.Select(async item =>
            {
                var (package, cabPath) = item;
                string subCorr = $"{corr}:{package.Language}";
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // Integrity pre-check: when the manifest advertised a digest, the
                    // downloaded bytes must match it before we trust the signature.
                    // A mismatch means a corrupted or substituted payload (e.g. a
                    // mirror returning the wrong file).
                    if (!string.IsNullOrWhiteSpace(package.Sha256))
                    {
                        string actual = ComputeSha256(cabPath);
                        if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            string q = Quarantine(cabPath, quarantineDir);
                            lock (quarantined) quarantined.Add(Path.GetFileName(cabPath));
                            LogLine(log, $"  ! {package.Language}: SHA-256 mismatch — rejected and quarantined.");
                            _logger.Warn("fod.integrity.mismatch", subCorr, Fields(("file", package.FileName), ("expected", package.Sha256), ("actual", actual), ("quarantine", q)));
                            return;
                        }
                        LogLine(log, $"  • {package.Language}: SHA-256 matches manifest.");
                    }

                    var verdict = await _verifier.VerifyAsync(cabPath, subCorr, cancellationToken).ConfigureAwait(false);
                    if (!verdict.IsTrusted)
                    {
                        string quarantinedPath = Quarantine(cabPath, quarantineDir);
                        lock (quarantined) quarantined.Add(Path.GetFileName(cabPath));
                        LogLine(log, $"  ! {package.Language}: signature {verdict.Status} — rejected and quarantined ({verdict.Error}).");
                        _logger.Warn("fod.verify.rejected", subCorr, Fields(("file", package.FileName), ("status", verdict.Status), ("quarantine", quarantinedPath)));
                        return;
                    }

                    LogLine(log, $"  • {package.Language}: signature OK ({verdict.Signer ?? "signed"}).");
                    string langExtractDir = Path.Combine(extractDir, package.Language);
                    var fonts = await _extractor.ExtractFontsAsync(cabPath, langExtractDir, cancellationToken).ConfigureAwait(false);
                    if (fonts.Count == 0)
                    {
                        LogLine(log, $"  ! {package.Language}: CAB contained no extractable font files.");
                        return;
                    }
                    perLanguage[package.Language] = fonts.ToList();
                    LogLine(log, $"  • {package.Language}: extracted {fonts.Count} font file(s).");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogLine(log, $"  ! {package.Language}: verify/extract failed: {ex.Message}");
                    _logger.Error("fod.extract.failed", subCorr, Fields(("file", package.FileName), ("error", ex.Message)));
                }
                finally
                {
                    int n = Interlocked.Increment(ref completed);
                    Report(progress, FoDStage.Extract, $"Processed {package.FileName}.", n, downloaded.Count, corr);
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return perLanguage;
        }

        // ---- Stage 5 ----------------------------------------------------------

        private async Task<HashSet<string>> InstallAsync(
            IReadOnlyDictionary<string, List<ExtractedFont>> extractedByLanguage,
            IReadOnlyDictionary<string, List<string>> requestedByLanguage,
            IUserFontInstaller installer,
            string corr,
            IProgress<FoDProgress>? progress,
            List<string> log,
            CancellationToken cancellationToken)
        {
            var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var installedFamilies = new HashSet<string>(StringComparer.Ordinal); // normalized
            var languagesWithAnyInstall = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var languagesWithParsedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int totalFonts = extractedByLanguage.Values.Sum(v => v.Count);
            int done = 0;

            foreach (var (language, fonts) in extractedByLanguage)
            {
                foreach (var font in fonts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    done++;

                    string installFamily = ChooseInstallFamily(font);
                    bool ok;
                    try
                    {
                        using var fs = File.OpenRead(font.FilePath);
                        ok = await installer.InstallFromStreamAsync(installFamily, font.FileName, fs, log, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        ok = false;
                        LogLine(log, $"  ! install failed for {font.FileName}: {ex.Message}");
                    }

                    Report(progress, FoDStage.Install, $"Installed {font.FileName}.", done, totalFonts, corr);
                    if (!ok) continue;

                    languagesWithAnyInstall.Add(language);
                    if (font.FamilyNames.Count > 0)
                    {
                        languagesWithParsedNames.Add(language);
                        foreach (var fam in font.FamilyNames) installedFamilies.Add(FontNameNormalizer.Normalize(fam));
                    }
                }
            }

            // Credit by exact family match first. The best-effort language fallback
            // applies ONLY when a language installed fonts but produced no parseable
            // family names at all — never when some files named themselves and a
            // requested font simply was not among them (which would be a false
            // positive that suppresses later fallbacks).
            foreach (var (language, requested) in requestedByLanguage)
            {
                bool unnamedLanguage = languagesWithAnyInstall.Contains(language)
                    && !languagesWithParsedNames.Contains(language);
                foreach (var font in requested)
                {
                    if (installedFamilies.Contains(FontNameNormalizer.Normalize(font)))
                    {
                        resolved.Add(font);
                    }
                    else if (unnamedLanguage)
                    {
                        resolved.Add(font);
                        LogLine(log, $"  • {font}: credited to installed {language} package (font names unparsed).");
                    }
                }
            }

            return resolved;
        }

        private static string ChooseInstallFamily(ExtractedFont font)
        {
            if (font.FamilyNames.Count > 0)
            {
                // A collection registers under all of its families joined by " & ",
                // matching the Windows font registry convention for .ttc files.
                return font.IsCollection
                    ? string.Join(" & ", font.FamilyNames)
                    : font.FamilyNames[0];
            }
            return Path.GetFileNameWithoutExtension(font.FileName);
        }

        // ---- helpers ----------------------------------------------------------

        private static FoDPipelineResult AllMissing(IReadOnlyList<string> input, List<string> log)
            => new FoDPipelineResult { Resolved = Array.Empty<string>(), StillMissing = input, Log = log };

        private static string Quarantine(string cabPath, string quarantineDir)
        {
            try
            {
                Directory.CreateDirectory(quarantineDir);
                string dest = Path.Combine(quarantineDir, Path.GetFileName(cabPath));
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(cabPath, dest);
                return dest;
            }
            catch
            {
                // If we cannot move it, at least make sure it is not left where it
                // could be mistaken for a verified CAB.
                TryDeleteFile(cabPath);
                return cabPath;
            }
        }

        private void Report(IProgress<FoDProgress>? progress, FoDStage stage, string message, int completed, int total, string corr)
            => progress?.Report(new FoDProgress { Stage = stage, Message = message, Completed = completed, Total = total, CorrelationId = corr });

        private static void LogLine(List<string> log, string line)
        {
            lock (log) log.Add(line);
        }

        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            }
            return new string(chars);
        }

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }

        private static void TryDeleteIfEmpty(string dir)
        {
            // Non-recursive: throws (and is swallowed) when the directory still
            // holds the quarantine sub-folder, which must be preserved.
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: false); } catch { }
        }

        private static string ComputeSha256(string path)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(fs));
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static IReadOnlyDictionary<string, object?> Fields(params (string Key, object? Value)[] pairs)
        {
            var d = new Dictionary<string, object?>(pairs.Length);
            foreach (var (k, v) in pairs) d[k] = v;
            return d;
        }
    }
}

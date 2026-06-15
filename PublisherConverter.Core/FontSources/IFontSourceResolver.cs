using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FontWorker;

namespace PublisherConverter.Core.FontSources
{
    /// <summary>Per-run state handed to each resolver (install sink, scratch, logging).</summary>
    public sealed class ResolverContext
    {
        public required IUserFontInstaller Installer { get; init; }
        public required string ScratchDirectory { get; init; }
        public required IList<string> Log { get; init; }
        public string? CorrelationId { get; init; }
        public IStructuredLogger Logger { get; init; } = NullStructuredLogger.Instance;
    }

    /// <summary>
    /// One layer of the fallback chain. The orchestrator calls resolvers in
    /// strict order and stops at the first whose result <see cref="FontAcquisitionResult.IsResolved"/>.
    /// A resolver never throws for an ordinary miss — it returns a structured
    /// Missing/ManualReview/Rejected result and logs at debug level.
    /// </summary>
    public interface IFontSourceResolver
    {
        ResolutionLayer Layer { get; }

        /// <summary>Whether this resolver participates given the current policy.</summary>
        bool IsEnabled { get; }

        Task<FontAcquisitionResult> TryResolveAsync(FontRequest request, ResolverContext context, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Shared plumbing for the remote resolvers: validate a candidate is a real
    /// .ttf, apply the license gate, stage to scratch, and install. Centralizing
    /// this keeps every resolver consistent about "never install a non-.ttf or an
    /// un-cleared license".
    /// </summary>
    public abstract class FontSourceResolverBase : IFontSourceResolver
    {
        protected FontLicenseEvaluator LicenseEvaluator { get; }
        protected IStructuredLogger Logger { get; }

        protected FontSourceResolverBase(FontLicenseEvaluator licenseEvaluator, IStructuredLogger? logger)
        {
            LicenseEvaluator = licenseEvaluator ?? throw new ArgumentNullException(nameof(licenseEvaluator));
            Logger = logger ?? NullStructuredLogger.Instance;
        }

        public abstract ResolutionLayer Layer { get; }
        public abstract bool IsEnabled { get; }
        public abstract Task<FontAcquisitionResult> TryResolveAsync(FontRequest request, ResolverContext context, CancellationToken cancellationToken);

        /// <summary>
        /// Validates bytes are a real .ttf, applies the license decision, and (if
        /// allowed) stages + installs the font. Returns the structured result.
        /// </summary>
        protected async Task<FontAcquisitionResult> FinalizeTtfAsync(
            FontRequest request,
            string sourceId,
            string sourceUrl,
            byte[] data,
            LicenseEvaluation license,
            double confidence,
            string styleToken,
            ResolverContext context,
            CancellationToken cancellationToken,
            string? licenseText = null)
        {
            var integrity = FontIntegrityValidator.Validate(data);
            if (!integrity.IsValidTtf)
            {
                Debug(context, $"{Layer}/{sourceId}: rejected payload — {integrity.Reason}");
                return Result(request, AcquisitionStatus.Rejected, sourceId, sourceUrl, confidence, styleToken, license.Status,
                    failure: $"integrity: {integrity.Reason}");
            }

            if (license.Status == LicenseStatus.Rejected)
            {
                Debug(context, $"{Layer}/{sourceId}: license rejected — {license.Reason}");
                return Result(request, AcquisitionStatus.Rejected, sourceId, sourceUrl, confidence, styleToken, license.Status,
                    failure: $"license: {license.Reason}");
            }
            if (license.Status == LicenseStatus.ManualReviewRequired)
            {
                Debug(context, $"{Layer}/{sourceId}: manual review required — {license.Reason}");
                string staged = StageToScratch(context, request, styleToken, data);
                return Result(request, AcquisitionStatus.ManualReviewRequired, sourceId, sourceUrl, confidence, styleToken, license.Status,
                    downloadedPath: staged, manualReview: true, failure: license.Reason, licenseText: licenseText);
            }

            // Allowed or NotApplicable → stage + install.
            string scratchPath = StageToScratch(context, request, styleToken, data);
            bool installed;
            try
            {
                using var fs = File.OpenRead(scratchPath);
                string installFamily = request.NormalizedFamily;
                installed = await context.Installer.InstallFromStreamAsync(installFamily, Path.GetFileName(scratchPath), fs, context.Log, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug(context, $"{Layer}/{sourceId}: install failed — {ex.Message}");
                return Result(request, AcquisitionStatus.Acquired, sourceId, sourceUrl, confidence, styleToken, license.Status,
                    downloadedPath: scratchPath, failure: $"install: {ex.Message}");
            }

            return Result(request,
                installed ? AcquisitionStatus.Installed : AcquisitionStatus.Acquired,
                sourceId, sourceUrl, confidence, styleToken, license.Status,
                downloadedPath: scratchPath,
                installedPath: installed ? scratchPath : null);
        }

        protected string StageToScratch(ResolverContext context, FontRequest request, string styleToken, byte[] data)
        {
            Directory.CreateDirectory(context.ScratchDirectory);
            string name = SafeFileName($"{request.NormalizedFamily}-{styleToken}.ttf");
            string path = Path.Combine(context.ScratchDirectory, name);
            File.WriteAllBytes(path, data);
            return path;
        }

        protected FontAcquisitionResult Result(
            FontRequest request, AcquisitionStatus status, string sourceId, string? sourceUrl,
            double confidence, string styleToken, LicenseStatus license,
            string? downloadedPath = null, string? installedPath = null, string? failure = null, bool manualReview = false,
            string? licenseText = null)
            => new FontAcquisitionResult
            {
                RequestedFontName = request.RequestedName,
                NormalizedFamily = request.NormalizedFamily,
                RequestedStyle = styleToken,
                Status = status,
                Layer = Layer,
                SourceId = sourceId,
                SourceUrl = sourceUrl,
                DownloadedFilePath = downloadedPath,
                InstalledFilePath = installedPath,
                License = license,
                MatchConfidence = confidence,
                FailureReason = failure,
                ManualReviewRequired = manualReview,
                LicenseText = licenseText,
            };

        protected void Debug(ResolverContext context, string message)
        {
            context.Log.Add($"    [debug] {message}");
            Logger.Log(StructuredLogLevel.Debug, "fontsource.debug", context.CorrelationId,
                new Dictionary<string, object?> { ["layer"] = Layer.ToString(), ["message"] = message });
        }

        protected static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            }
            return new string(chars);
        }
    }
}

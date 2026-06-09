using System;

namespace PublisherConverter.Core.FontSources
{
    /// <summary>Structured license decision plus the reason behind it.</summary>
    public sealed class LicenseEvaluation
    {
        public required LicenseStatus Status { get; init; }
        public string? Reason { get; init; }

        public bool AllowsAutoInstall => Status == LicenseStatus.Allowed || Status == LicenseStatus.NotApplicable;
    }

    /// <summary>
    /// Classifies a font payload's license from a source hint and/or license text,
    /// driven entirely by the configured keyword lists and policy. Microsoft and
    /// other trusted sources pass <see cref="LicenseStatus.NotApplicable"/>;
    /// community payloads must produce a clear Allowed verdict (and the policy
    /// master switch must permit it) or they are held for manual review.
    /// </summary>
    public sealed class FontLicenseEvaluator
    {
        private readonly LicensePolicyOptions _policy;

        public FontLicenseEvaluator(LicensePolicyOptions policy)
        {
            _policy = policy ?? new LicensePolicyOptions();
        }

        /// <summary>
        /// Evaluates a license. <paramref name="trustedSource"/> short-circuits to
        /// NotApplicable for Microsoft/Google-class sources. Otherwise the hint and
        /// text are scanned: reject keywords win, then allowed, then manual-review;
        /// an absent/unknown signal follows the configured unknown-license action.
        /// </summary>
        public LicenseEvaluation Evaluate(string? licenseHint, string? licenseText, bool trustedSource)
        {
            if (trustedSource)
                return new LicenseEvaluation { Status = LicenseStatus.NotApplicable, Reason = "trusted source" };

            string haystack = ((licenseHint ?? string.Empty) + "\n" + (licenseText ?? string.Empty));
            bool hasAnySignal = !string.IsNullOrWhiteSpace(licenseHint) || !string.IsNullOrWhiteSpace(licenseText);

            // Reject keywords take precedence — a clearly-forbidden payload is out.
            if (ContainsAny(haystack, _policy.RejectKeywords, out var rejectHit))
                return Decide(LicenseStatus.Rejected, $"matched reject keyword '{rejectHit}'");

            bool allowed = ContainsAny(haystack, _policy.AllowedKeywords, out var allowHit);
            bool manual = ContainsAny(haystack, _policy.ManualReviewKeywords, out var manualHit);

            // A permissive license still wins over a personal-use phrase only when
            // the personal-use phrase is absent (e.g. an OFL that also says "free").
            if (allowed && !manual)
                return Decide(LicenseStatus.Allowed, $"matched allowed keyword '{allowHit}'");
            if (manual)
                return Decide(LicenseStatus.ManualReviewRequired, $"matched manual-review keyword '{manualHit}'");
            if (allowed)
                return Decide(LicenseStatus.Allowed, $"matched allowed keyword '{allowHit}'");

            if (!hasAnySignal)
                return Decide(MapUnknown(), "no license signal found");

            return Decide(MapUnknown(), "license present but unrecognized");
        }

        private LicenseEvaluation Decide(LicenseStatus status, string reason)
        {
            // Master switch: when auto-install of non-Microsoft fonts is disabled,
            // never return Allowed — downgrade to manual review.
            if (status == LicenseStatus.Allowed && !_policy.AutoInstallNonMicrosoft)
                return new LicenseEvaluation { Status = LicenseStatus.ManualReviewRequired, Reason = "auto-install disabled by policy" };
            return new LicenseEvaluation { Status = status, Reason = reason };
        }

        private LicenseStatus MapUnknown() => _policy.UnknownLicenseAction switch
        {
            UnknownLicenseAction.Allow => LicenseStatus.Allowed,
            UnknownLicenseAction.Reject => LicenseStatus.Rejected,
            _ => LicenseStatus.ManualReviewRequired,
        };

        private static bool ContainsAny(string haystack, System.Collections.Generic.IEnumerable<string> needles, out string? hit)
        {
            foreach (var n in needles)
            {
                if (string.IsNullOrWhiteSpace(n)) continue;
                if (haystack.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hit = n;
                    return true;
                }
            }
            hit = null;
            return false;
        }
    }
}

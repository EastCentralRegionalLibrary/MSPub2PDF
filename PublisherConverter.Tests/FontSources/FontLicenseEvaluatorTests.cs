using PublisherConverter.Core.FontSources;
using Xunit;

namespace PublisherConverter.Tests.FontSources
{
    public sealed class FontLicenseEvaluatorTests
    {
        private static FontLicenseEvaluator New(LicensePolicyOptions? p = null) => new FontLicenseEvaluator(p ?? new LicensePolicyOptions());

        [Fact]
        public void Trusted_source_is_not_applicable()
        {
            var e = New().Evaluate(licenseHint: null, licenseText: null, trustedSource: true);
            Assert.Equal(LicenseStatus.NotApplicable, e.Status);
            Assert.True(e.AllowsAutoInstall);
        }

        [Fact]
        public void Ofl_hint_is_allowed()
        {
            var e = New().Evaluate(licenseHint: "SIL Open Font License (OFL)", licenseText: null, trustedSource: false);
            Assert.Equal(LicenseStatus.Allowed, e.Status);
        }

        [Fact]
        public void Personal_use_text_requires_manual_review()
        {
            var e = New().Evaluate(licenseHint: null, licenseText: "This font is free for personal use only.", trustedSource: false);
            Assert.Equal(LicenseStatus.ManualReviewRequired, e.Status);
        }

        [Fact]
        public void Reject_keyword_wins_over_allowed()
        {
            var e = New().Evaluate(licenseHint: "OFL", licenseText: "commercial use prohibited", trustedSource: false);
            Assert.Equal(LicenseStatus.Rejected, e.Status);
        }

        [Fact]
        public void Unknown_license_follows_policy_action()
        {
            var manual = New(new LicensePolicyOptions { UnknownLicenseAction = UnknownLicenseAction.ManualReviewRequired })
                .Evaluate(null, null, trustedSource: false);
            Assert.Equal(LicenseStatus.ManualReviewRequired, manual.Status);

            var reject = New(new LicensePolicyOptions { UnknownLicenseAction = UnknownLicenseAction.Reject })
                .Evaluate(null, "some unrecognized text", trustedSource: false);
            Assert.Equal(LicenseStatus.Rejected, reject.Status);
        }

        [Fact]
        public void Auto_install_disabled_downgrades_allowed_to_manual()
        {
            var policy = new LicensePolicyOptions { AutoInstallNonMicrosoft = false };
            var e = New(policy).Evaluate(licenseHint: "OFL", licenseText: null, trustedSource: false);
            Assert.Equal(LicenseStatus.ManualReviewRequired, e.Status);
        }
    }
}

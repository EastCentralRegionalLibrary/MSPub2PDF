using System;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FontWorker;

namespace PublisherConverter.Core.FeaturesOnDemand
{
    /// <summary>
    /// Production <see cref="ICabSignatureVerifier"/> that selects the platform
    /// backend at runtime: native <c>WinVerifyTrust</c> on Windows, the managed
    /// <see cref="SignedCmsCabSignatureVerifier"/> elsewhere.
    /// </summary>
    public sealed class CrossPlatformCabSignatureVerifier : ICabSignatureVerifier
    {
        private readonly ICabSignatureVerifier _inner;

        public CrossPlatformCabSignatureVerifier(IStructuredLogger? logger = null)
        {
            _inner = OperatingSystem.IsWindows()
                ? new WindowsAuthenticodeCabVerifier()
                // Off-Windows the Microsoft roots are usually absent from the trust
                // store, so a valid-but-untrusted-root chain is permitted there.
                : new SignedCmsCabSignatureVerifier(allowUntrustedRoot: true, logger: logger);
        }

        public Task<SignatureVerificationResult> VerifyAsync(string filePath, string? correlationId, CancellationToken cancellationToken)
            => _inner.VerifyAsync(filePath, correlationId, cancellationToken);
    }
}

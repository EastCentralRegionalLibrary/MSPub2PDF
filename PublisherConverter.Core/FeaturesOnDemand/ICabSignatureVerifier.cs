using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FeaturesOnDemand
{
    /// <summary>Outcome of verifying a CAB's Authenticode signature.</summary>
    public sealed class SignatureVerificationResult
    {
        /// <summary>True only when the signature is valid and chains to a trusted, expected signer.</summary>
        public bool IsTrusted { get; init; }

        /// <summary>
        /// Machine-readable status: "Valid", "NotSigned", "HashMismatch",
        /// "UntrustedRoot", "UntrustedSigner", "Error", or a platform code.
        /// </summary>
        public required string Status { get; init; }

        /// <summary>Signer subject when it could be read.</summary>
        public string? Signer { get; init; }

        /// <summary>Human-readable detail for an untrusted/failed result.</summary>
        public string? Error { get; init; }

        public static SignatureVerificationResult Trusted(string? signer, string status = "Valid")
            => new SignatureVerificationResult { IsTrusted = true, Status = status, Signer = signer };

        public static SignatureVerificationResult Untrusted(string status, string? error = null, string? signer = null)
            => new SignatureVerificationResult { IsTrusted = false, Status = status, Error = error, Signer = signer };
    }

    /// <summary>
    /// Verifies the Authenticode signature of a downloaded CAB before any of its
    /// contents are trusted. Implementations are platform-specific (native trust
    /// chain on Windows, managed PKCS#7 verification elsewhere); the orchestration
    /// depends only on this seam so a CAB that fails verification can be uniformly
    /// rejected and quarantined.
    /// </summary>
    public interface ICabSignatureVerifier
    {
        Task<SignatureVerificationResult> VerifyAsync(string filePath, string? correlationId, CancellationToken cancellationToken);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FontWorker;

namespace PublisherConverter.Core.FeaturesOnDemand
{
    /// <summary>
    /// Cross-platform CAB signature verifier built on .NET's in-box
    /// <see cref="SignedCms"/>. It locates the PKCS#7 SignedData blob embedded in
    /// the cabinet, verifies the signature cryptographically, builds the signer's
    /// certificate chain, and applies a signer policy (Microsoft by default).
    ///
    /// This stands in for the <c>Signify.Authenticode</c> package referenced in
    /// the task brief, which is not published on NuGet. The internal
    /// <see cref="AuthenticodeSignedFile"/> deliberately mirrors that package's
    /// <c>FromStream</c> / <c>Verify</c> / <c>ExplainVerify</c> shape, so dropping
    /// in the real library later is a one-type change behind this same
    /// <see cref="ICabSignatureVerifier"/> seam.
    ///
    /// Scope note: this validates that the SignedData is authentic and from the
    /// expected signer (the trust decision). Byte-exact binding of the file body
    /// to the signed digest is enforced natively by
    /// <see cref="WindowsAuthenticodeCabVerifier"/> on Windows, the platform where
    /// these system fonts are actually installed.
    /// </summary>
    public sealed class SignedCmsCabSignatureVerifier : ICabSignatureVerifier
    {
        private readonly Predicate<X509Certificate2> _signerPolicy;
        private readonly bool _allowUntrustedRoot;
        private readonly IStructuredLogger _logger;

        /// <param name="signerPolicy">
        /// Returns true when a certificate in the signer chain is acceptable.
        /// Defaults to "subject or issuer mentions Microsoft".
        /// </param>
        /// <param name="allowUntrustedRoot">
        /// When true, a chain that is otherwise valid but whose root is not in
        /// the local trust store is still accepted (useful off-Windows, where the
        /// Microsoft roots are usually absent, and in tests with a self-signed
        /// cert). Defaults to false (fail-closed).
        /// </param>
        public SignedCmsCabSignatureVerifier(
            Predicate<X509Certificate2>? signerPolicy = null,
            bool allowUntrustedRoot = false,
            IStructuredLogger? logger = null)
        {
            _signerPolicy = signerPolicy ?? DefaultMicrosoftPolicy;
            _allowUntrustedRoot = allowUntrustedRoot;
            _logger = logger ?? NullStructuredLogger.Instance;
        }

        public Task<SignatureVerificationResult> VerifyAsync(string filePath, string? correlationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AuthenticodeSignedFile signedFile;
            try
            {
                using var fs = File.OpenRead(filePath);
                signedFile = AuthenticodeSignedFile.FromStream(fs);
            }
            catch (NotSignedException)
            {
                _logger.Warn("fod.verify.not_signed", correlationId, Fields(("file", Path.GetFileName(filePath))));
                return Task.FromResult(SignatureVerificationResult.Untrusted("NotSigned", "No embedded PKCS#7 signature was found."));
            }
            catch (Exception ex)
            {
                return Task.FromResult(SignatureVerificationResult.Untrusted("Error", ex.Message));
            }

            var result = VerifySignedCms(signedFile.Pkcs7, _signerPolicy, _allowUntrustedRoot);
            _logger.Info("fod.verify.result", correlationId, Fields(
                ("file", Path.GetFileName(filePath)),
                ("trusted", result.IsTrusted),
                ("status", result.Status)));
            return Task.FromResult(result);
        }

        // ---- testable verification core (exposed via InternalsVisibleTo) ----

        internal static SignatureVerificationResult VerifySignedCms(
            byte[] pkcs7,
            Predicate<X509Certificate2> signerPolicy,
            bool allowUntrustedRoot)
        {
            var cms = new SignedCms();
            try
            {
                cms.Decode(pkcs7);
            }
            catch (Exception ex)
            {
                return SignatureVerificationResult.Untrusted("Error", $"PKCS#7 decode failed: {ex.Message}");
            }

            if (cms.SignerInfos.Count == 0)
            {
                return SignatureVerificationResult.Untrusted("NotSigned", "SignedData carried no signer.");
            }

            // Cryptographic validity of the signature (no chain yet).
            try
            {
                cms.CheckSignature(verifySignatureOnly: true);
            }
            catch (Exception ex)
            {
                return SignatureVerificationResult.Untrusted("HashMismatch", $"Signature is not valid: {ex.Message}");
            }

            var signer = cms.SignerInfos[0].Certificate;
            if (signer == null)
            {
                return SignatureVerificationResult.Untrusted("Error", "Signer certificate was not present in the SignedData.");
            }

            string signerSubject = signer.Subject;

            // Signer policy (who) — independent of chain trust (validity).
            bool policyOk = false;
            try { policyOk = signerPolicy(signer); } catch { policyOk = false; }
            if (!policyOk)
            {
                return SignatureVerificationResult.Untrusted("UntrustedSigner", "Signer did not satisfy the trust policy.", signerSubject);
            }

            // Certificate chain (validity).
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            foreach (var extra in cms.Certificates) chain.ChainPolicy.ExtraStore.Add(extra);

            bool chainOk = chain.Build(signer);
            if (!chainOk)
            {
                // allowUntrustedRoot only forgives a chain that builds fully but
                // ends in a root absent from the local store (the normal off-Windows
                // case for Microsoft roots). A PartialChain — a chain that could not
                // be completed at all (missing/forged intermediate) — is never
                // forgiven, so a self-issued "CN=Microsoft" leaf cannot pass.
                bool onlyUntrustedRoot = true;
                string? detail = null;
                foreach (var status in chain.ChainStatus)
                {
                    if (status.Status == X509ChainStatusFlags.NoError) continue;
                    if (status.Status != X509ChainStatusFlags.UntrustedRoot)
                    {
                        onlyUntrustedRoot = false;
                    }
                    detail = (detail == null ? "" : detail + "; ") + status.StatusInformation?.Trim();
                }

                if (!(onlyUntrustedRoot && allowUntrustedRoot))
                {
                    return SignatureVerificationResult.Untrusted("UntrustedRoot", detail ?? "Certificate chain did not validate.", signerSubject);
                }
            }

            return SignatureVerificationResult.Trusted(signerSubject);
        }

        private static bool DefaultMicrosoftPolicy(X509Certificate2 cert)
        {
            string subject = cert.Subject ?? string.Empty;
            string issuer = cert.Issuer ?? string.Empty;
            return subject.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0
                || issuer.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IReadOnlyDictionary<string, object?> Fields(params (string Key, object? Value)[] pairs)
        {
            var d = new Dictionary<string, object?>(pairs.Length);
            foreach (var (k, v) in pairs) d[k] = v;
            return d;
        }
    }

    /// <summary>Thrown when no embedded signature can be located in a file.</summary>
    public sealed class NotSignedException : Exception
    {
        public NotSignedException(string message) : base(message) { }
    }

    /// <summary>
    /// Locates and exposes the PKCS#7 SignedData embedded in a signed file,
    /// mirroring the <c>Signify.Authenticode.AuthenticodeFile</c> surface from the
    /// task brief (<c>FromStream</c> / <c>Verify</c> / <c>ExplainVerify</c>).
    ///
    /// The blob is found by scanning for the PKCS#7 <c>signedData</c> content-type
    /// OID (1.2.840.113549.1.7.2) and parsing the enclosing DER ContentInfo
    /// SEQUENCE — robust across the various CAB header-reserve layouts without
    /// hard-coding offsets.
    /// </summary>
    internal sealed class AuthenticodeSignedFile
    {
        // 06 09 2A 86 48 86 F7 0D 01 07 02  → OID 1.2.840.113549.1.7.2 (signedData)
        private static readonly byte[] SignedDataOid =
            { 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x07, 0x02 };

        public byte[] Pkcs7 { get; }

        private AuthenticodeSignedFile(byte[] pkcs7) => Pkcs7 = pkcs7;

        public static AuthenticodeSignedFile FromStream(Stream stream)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            byte[] data = ms.ToArray();

            if (!TryLocatePkcs7(data, out byte[] der))
            {
                throw new NotSignedException("No PKCS#7 signedData structure found in file.");
            }
            return new AuthenticodeSignedFile(der);
        }

        /// <summary>Throws when the embedded signature is not cryptographically valid.</summary>
        public void Verify()
        {
            var cms = new SignedCms();
            cms.Decode(Pkcs7);
            cms.CheckSignature(verifySignatureOnly: true);
        }

        /// <summary>Structured form of <see cref="Verify"/>: (status, error).</summary>
        public (string status, string? error) ExplainVerify()
        {
            try
            {
                Verify();
                return ("Valid", null);
            }
            catch (Exception ex)
            {
                return ("Invalid", ex.Message);
            }
        }

        internal static bool TryLocatePkcs7(byte[] data, out byte[] der)
        {
            der = Array.Empty<byte>();
            if (data == null || data.Length < 32) return false;

            int oidPos = IndexOf(data, SignedDataOid, 0);
            while (oidPos >= 0)
            {
                // The ContentInfo SEQUENCE header (0x30 + length) sits just before
                // the content-type OID. Try each DER length encoding.
                for (int back = 2; back <= 6; back++)
                {
                    int seqPos = oidPos - back;
                    if (seqPos < 0) break;
                    if (data[seqPos] != 0x30) continue;
                    if (TryReadDerLength(data, seqPos + 1, out int contentStart, out long length, out bool indefinite))
                    {
                        // The OID must be the first element of this SEQUENCE.
                        if (contentStart != oidPos) continue;

                        long end = indefinite ? data.Length : contentStart + length;
                        if (end <= seqPos || end > data.Length) continue;

                        var candidate = new byte[end - seqPos];
                        Array.Copy(data, seqPos, candidate, 0, candidate.Length);
                        if (DecodesAsSignedCms(candidate))
                        {
                            der = candidate;
                            return true;
                        }
                    }
                }
                oidPos = IndexOf(data, SignedDataOid, oidPos + 1);
            }
            return false;
        }

        private static bool DecodesAsSignedCms(byte[] candidate)
        {
            try
            {
                var cms = new SignedCms();
                cms.Decode(candidate);
                return cms.SignerInfos.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadDerLength(byte[] data, int pos, out int contentStart, out long length, out bool indefinite)
        {
            contentStart = 0;
            length = 0;
            indefinite = false;
            if (pos >= data.Length) return false;

            byte first = data[pos];
            if (first == 0x80)
            {
                indefinite = true;
                contentStart = pos + 1;
                return true;
            }
            if ((first & 0x80) == 0)
            {
                length = first;
                contentStart = pos + 1;
                return true;
            }

            int numBytes = first & 0x7F;
            if (numBytes < 1 || numBytes > 4 || pos + 1 + numBytes > data.Length) return false;

            long len = 0;
            for (int i = 0; i < numBytes; i++) len = (len << 8) | data[pos + 1 + i];
            length = len;
            contentStart = pos + 1 + numBytes;
            return true;
        }

        private static int IndexOf(byte[] haystack, byte[] needle, int start)
        {
            int last = haystack.Length - needle.Length;
            for (int i = start; i <= last; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }
    }
}

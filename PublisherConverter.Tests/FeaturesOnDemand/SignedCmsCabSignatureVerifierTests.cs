using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core.FeaturesOnDemand;
using Xunit;

namespace PublisherConverter.Tests.FeaturesOnDemand
{
    public sealed class SignedCmsCabSignatureVerifierTests
    {
        private static readonly byte[] Content = Encoding.ASCII.GetBytes("the cab body that was signed");

        [Fact]
        public void Valid_signature_with_matching_policy_is_trusted()
        {
            var (pkcs7, cert) = SignedCmsBuilder.Build("Microsoft Windows Test", Content);
            using (cert)
            {
                var result = SignedCmsCabSignatureVerifier.VerifySignedCms(pkcs7, _ => true, allowUntrustedRoot: true);

                Assert.True(result.IsTrusted);
                Assert.Equal("Valid", result.Status);
                Assert.Contains("Microsoft", result.Signer);
            }
        }

        [Fact]
        public void Untrusted_root_is_rejected_when_not_allowed()
        {
            var (pkcs7, cert) = SignedCmsBuilder.Build("Microsoft Windows Test", Content);
            using (cert)
            {
                var result = SignedCmsCabSignatureVerifier.VerifySignedCms(pkcs7, _ => true, allowUntrustedRoot: false);

                Assert.False(result.IsTrusted);
                Assert.Equal("UntrustedRoot", result.Status);
            }
        }

        [Fact]
        public void Signer_policy_mismatch_is_untrusted()
        {
            var (pkcs7, cert) = SignedCmsBuilder.Build("Some Other Vendor", Content);
            using (cert)
            {
                var result = SignedCmsCabSignatureVerifier.VerifySignedCms(pkcs7, _ => false, allowUntrustedRoot: true);

                Assert.False(result.IsTrusted);
                Assert.Equal("UntrustedSigner", result.Status);
            }
        }

        [Fact]
        public void Tampered_pkcs7_is_not_trusted()
        {
            var (pkcs7, cert) = SignedCmsBuilder.Build("Microsoft Windows Test", Content);
            using (cert)
            {
                // Corrupt a swathe of bytes near the end (signature/content region).
                for (int i = pkcs7.Length - 40; i < pkcs7.Length - 8; i++) pkcs7[i] ^= 0xFF;

                var result = SignedCmsCabSignatureVerifier.VerifySignedCms(pkcs7, _ => true, allowUntrustedRoot: true);

                Assert.False(result.IsTrusted);
            }
        }

        [Fact]
        public async Task DefaultMicrosoftPolicy_trusts_microsoft_subject_via_full_verify()
        {
            var (pkcs7, cert) = SignedCmsBuilder.Build("Microsoft Corporation", Content);
            using (cert)
            {
                // Default policy (Microsoft) + allow untrusted root for the self-signed cert.
                var verifier = new SignedCmsCabSignatureVerifier(allowUntrustedRoot: true);
                // Embed the PKCS#7 in a buffer and verify via the public file path API.
                string path = Path.GetTempFileName();
                try
                {
                    await File.WriteAllBytesAsync(path, Wrap(pkcs7));
                    var result = await verifier.VerifyAsync(path, "corr", CancellationToken.None);
                    Assert.True(result.IsTrusted);
                }
                finally { File.Delete(path); }
            }
        }

        [Fact]
        public async Task Unsigned_file_reports_not_signed()
        {
            var verifier = new SignedCmsCabSignatureVerifier(allowUntrustedRoot: true);
            string path = Path.GetTempFileName();
            try
            {
                await File.WriteAllBytesAsync(path, new byte[] { 0x4D, 0x53, 0x43, 0x46, 1, 2, 3, 4, 5, 6, 7, 8 });
                var result = await verifier.VerifyAsync(path, null, CancellationToken.None);
                Assert.False(result.IsTrusted);
                Assert.Equal("NotSigned", result.Status);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void AuthenticodeSignedFile_locates_embedded_pkcs7()
        {
            var (pkcs7, cert) = SignedCmsBuilder.Build("Microsoft", Content);
            using (cert)
            {
                using var ms = new MemoryStream(Wrap(pkcs7));
                var signed = AuthenticodeSignedFile.FromStream(ms);

                var (status, error) = signed.ExplainVerify();
                Assert.Equal("Valid", status);
                Assert.Null(error);
            }
        }

        // Embed the PKCS#7 between filler bytes, the way a CAB carries it.
        private static byte[] Wrap(byte[] pkcs7)
        {
            var prefix = new byte[] { 0x4D, 0x53, 0x43, 0x46, 0, 0, 0, 0, 0xDE, 0xAD, 0xBE, 0xEF };
            var suffix = new byte[] { 0x00, 0x11, 0x22, 0x33 };
            return prefix.Concat(pkcs7).Concat(suffix).ToArray();
        }
    }
}

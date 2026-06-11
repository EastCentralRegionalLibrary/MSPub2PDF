using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FeaturesOnDemand
{
    /// <summary>
    /// Native Windows CAB verifier. Uses <c>WinVerifyTrust</c> with the generic
    /// Authenticode policy provider, which validates both the embedded signature
    /// (file-hash binding) and the full certificate trust chain exactly as the
    /// OS would for any signed system component.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsAuthenticodeCabVerifier : ICabSignatureVerifier
    {
        private static readonly Guid WinTrustActionGenericVerifyV2 =
            new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE = 2;
        private const uint WTD_REVOCATION_CHECK_NONE = 0x00000010;

        private const uint ERROR_SUCCESS = 0;
        private const uint TRUST_E_NOSIGNATURE = 0x800B0100;
        private const uint TRUST_E_BAD_DIGEST = 0x80096010;
        private const uint CERT_E_UNTRUSTEDROOT = 0x800B0109;
        private const uint TRUST_E_SUBJECT_NOT_TRUSTED = 0x800B0004;
        private const uint CERT_E_EXPIRED = 0x800B0101;

        public Task<SignatureVerificationResult> VerifyAsync(string filePath, string? correlationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            uint hr = InvokeWinVerifyTrust(filePath);
            string? signer = TryReadSigner(filePath);

            SignatureVerificationResult result = hr switch
            {
                ERROR_SUCCESS => SignatureVerificationResult.Trusted(signer),
                TRUST_E_NOSIGNATURE => SignatureVerificationResult.Untrusted("NotSigned", "File is not signed.", signer),
                TRUST_E_BAD_DIGEST => SignatureVerificationResult.Untrusted("HashMismatch", "File digest does not match the signature.", signer),
                CERT_E_UNTRUSTEDROOT => SignatureVerificationResult.Untrusted("UntrustedRoot", "Certificate chain terminates in an untrusted root.", signer),
                TRUST_E_SUBJECT_NOT_TRUSTED => SignatureVerificationResult.Untrusted("UntrustedSigner", "Subject is not trusted.", signer),
                CERT_E_EXPIRED => SignatureVerificationResult.Untrusted("Expired", "A certificate in the chain has expired.", signer),
                _ => SignatureVerificationResult.Untrusted($"0x{hr:X8}", $"WinVerifyTrust returned 0x{hr:X8}.", signer),
            };

            return Task.FromResult(result);
        }

        private static uint InvokeWinVerifyTrust(string filePath)
        {
            IntPtr pFilePath = IntPtr.Zero;
            IntPtr pFileInfo = IntPtr.Zero;
            IntPtr pWvtData = IntPtr.Zero;
            try
            {
                pFilePath = Marshal.StringToHGlobalUni(filePath);

                var fileInfo = new WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                    pcwszFilePath = pFilePath,
                    hFile = IntPtr.Zero,
                    pgKnownSubject = IntPtr.Zero,
                };
                pFileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
                Marshal.StructureToPtr(fileInfo, pFileInfo, false);

                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = pFileInfo,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    dwProvFlags = WTD_REVOCATION_CHECK_NONE,
                };
                pWvtData = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
                Marshal.StructureToPtr(data, pWvtData, false);

                Guid action = WinTrustActionGenericVerifyV2;
                int hr = WinVerifyTrust(IntPtr.Zero, ref action, pWvtData);

                // Release the trust-provider state regardless of the verdict. The
                // VERIFY call wrote the allocated state handle into the NATIVE
                // struct's hWVTStateData, so read that back (rather than the stale
                // local copy, whose handle is still zero) before issuing CLOSE —
                // otherwise the per-call provider state is leaked.
                var verified = Marshal.PtrToStructure<WINTRUST_DATA>(pWvtData);
                verified.dwStateAction = WTD_STATEACTION_CLOSE;
                Marshal.StructureToPtr(verified, pWvtData, false);
                WinVerifyTrust(IntPtr.Zero, ref action, pWvtData);

                return unchecked((uint)hr);
            }
            finally
            {
                if (pWvtData != IntPtr.Zero) Marshal.FreeHGlobal(pWvtData);
                if (pFileInfo != IntPtr.Zero) Marshal.FreeHGlobal(pFileInfo);
                if (pFilePath != IntPtr.Zero) Marshal.FreeHGlobal(pFilePath);
            }
        }

        private static string? TryReadSigner(string filePath)
        {
            try
            {
                using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
                return cert.Subject;
            }
            catch
            {
                return null;
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
        }
    }
}

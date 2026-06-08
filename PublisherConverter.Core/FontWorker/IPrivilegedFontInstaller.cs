using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FontWorker
{
    /// <summary>
    /// Executes the privileged font-provisioning operations. This is the only
    /// component that touches machine-wide state (Windows capabilities, the
    /// system fonts directory, HKLM) and it only ever runs inside the elevated
    /// worker process. Abstracted so the worker host and the whole IPC pipeline
    /// can be tested on Linux against a fake installer.
    ///
    /// Every operation is idempotent: installing an already-present resource
    /// reports <see cref="InstallOutcome.AlreadyPresent"/> and never errors.
    /// </summary>
    public interface IPrivilegedFontInstaller
    {
        Task<InstallCapabilityResponse> InstallCapabilityAsync(InstallCapabilityRequest request, string correlationId, CancellationToken cancellationToken);

        Task<InstallCapabilitiesBatchResponse> InstallCapabilitiesBatchAsync(InstallCapabilitiesBatchRequest request, string correlationId, CancellationToken cancellationToken);

        Task<InstallFontResponse> InstallFontAsync(InstallFontRequest request, string correlationId, CancellationToken cancellationToken);
    }
}

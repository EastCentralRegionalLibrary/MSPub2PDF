using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FontWorker
{
    /// <summary>
    /// Per-cycle font-provisioning configuration derived from user settings.
    /// Controls how the coordinator routes installs; the strict/notify decision
    /// (whether to fail a document with unresolved fonts) belongs to the
    /// orchestrator and travels separately on <see cref="ConversionOptions"/>.
    /// </summary>
    public sealed class FontProvisioningPolicy
    {
        /// <summary>
        /// Whether provisioning is attempted at all. When false the coordinator
        /// only detects missing fonts and resolves nothing.
        /// </summary>
        public bool AutomaticInstallEnabled { get; init; }

        /// <summary>
        /// Whether the elevated worker may be launched. When false, only
        /// user-level installs are performed and capability-only fonts cannot
        /// be resolved.
        /// </summary>
        public bool AllowElevatedInstall { get; init; }

        public static FontProvisioningPolicy DetectOnly { get; } = new FontProvisioningPolicy
        {
            AutomaticInstallEnabled = false,
            AllowElevatedInstall = false,
        };
    }

    /// <summary>
    /// Optional lifecycle seam implemented by font resolvers that own a
    /// session-scoped elevated worker. The orchestrator opens a cycle before a
    /// batch and closes it afterwards so the worker is launched at most once per
    /// cycle and torn down when the batch ends — without disposing the resolver,
    /// which is reused across runs (the GUI's "Continue" flow).
    /// </summary>
    public interface IFontProvisioningSession
    {
        Task BeginCycleAsync(FontProvisioningPolicy policy, CancellationToken cancellationToken);

        Task EndCycleAsync(CancellationToken cancellationToken);
    }
}

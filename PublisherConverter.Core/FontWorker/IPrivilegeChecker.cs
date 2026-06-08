using System.Runtime.Versioning;
using System.Security.Principal;

namespace PublisherConverter.Core.FontWorker
{
    /// <summary>
    /// Reports whether the current process is running with administrator
    /// privileges. Abstracted so privilege-dependent code paths can be unit
    /// tested on Linux without any real elevation.
    /// </summary>
    public interface IPrivilegeChecker
    {
        bool IsCurrentProcessElevated();
    }

    /// <summary>
    /// Fixed-answer privilege checker for tests and non-Windows hosts. The
    /// production worker uses <see cref="WindowsPrivilegeChecker"/>; everywhere
    /// else this lets callers pin the answer deterministically.
    /// </summary>
    public sealed class StaticPrivilegeChecker : IPrivilegeChecker
    {
        private readonly bool _elevated;

        public StaticPrivilegeChecker(bool elevated)
        {
            _elevated = elevated;
        }

        public bool IsCurrentProcessElevated() => _elevated;
    }

    /// <summary>
    /// Real Windows elevation check via the process token's group membership in
    /// the built-in Administrators role.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class WindowsPrivilegeChecker : IPrivilegeChecker
    {
        public bool IsCurrentProcessElevated()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }
}

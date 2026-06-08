using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    /// <summary>
    /// One way of making a missing font available: a Windows optional-font
    /// capability install, a downloadable fallback, a future Google Fonts
    /// lookup, etc. Strategies are tried in order by the FontResolver until
    /// one succeeds for a given font.
    ///
    /// All strategies are best-effort: TryResolveAsync swallows its own
    /// failures and returns false, never throws into the conversion pipeline.
    /// </summary>
    public interface IFontProvisioningStrategy
    {
        /// <summary>Short human-readable name for log lines.</summary>
        string Name { get; }

        /// <summary>True if this strategy knows how to attempt the given font.</summary>
        bool CanResolve(string fontFamily);

        /// <summary>
        /// Best-effort provisioning. Appends one or more log lines describing
        /// what was tried. Returns true only on a verified install.
        /// </summary>
        Task<bool> TryResolveAsync(string fontFamily, IList<string> log, CancellationToken cancellationToken);
    }
}

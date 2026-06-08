using System.Collections.Generic;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Pre-flight check that returns the list of fonts referenced by a .pub
    /// publication and not currently installed on the local system, without
    /// asking Publisher to open the file. Publisher crashes during export
    /// when a referenced font is missing and substitution rules aren't set,
    /// so the orchestrator uses this to short-circuit the render attempt.
    /// </summary>
    public interface IFontAuditor
    {
        /// <summary>
        /// Returns the missing-font family names, alphabetically. An empty
        /// list means every referenced font is installed (or that the file
        /// has no resolvable font references, e.g. not an OLE container).
        /// </summary>
        IReadOnlyList<string> ResolveMissingFonts(string pubPath);

        /// <summary>
        /// Convenience surface that writes the audit outcome onto a
        /// <see cref="RenderResult"/>. Used by tests that follow the
        /// "populate-the-result" style; the orchestrator uses the list
        /// directly via <see cref="ResolveMissingFonts"/>.
        /// </summary>
        void AuditFonts(string pubPath, RenderResult result);
    }
}

namespace PublisherConverter.Core
{
    /// <summary>
    /// Reports whether a font family is installed on the local system. The
    /// production implementation enumerates the Windows font registry; tests
    /// substitute a fake with a fixed stock list.
    ///
    /// Forward compatibility: the automatic font-installer feature sits
    /// alongside this provider — the audit step still asks "is it installed?",
    /// but on a "no" the resolver attempts to provision the font and then the
    /// cache re-evaluates. Today's surface (Normalize + IsInstalled) is what
    /// that code reuses.
    /// </summary>
    public interface IInstalledFontProvider
    {
        bool IsInstalled(string family);
    }

    /// <summary>
    /// An installed-font provider that can re-read its underlying source on
    /// demand. The auto-resolver calls Refresh (via the availability cache)
    /// after installing fonts so newly-registered families are visible.
    /// </summary>
    public interface IRefreshableInstalledFontProvider : IInstalledFontProvider
    {
        void Refresh();
    }
}

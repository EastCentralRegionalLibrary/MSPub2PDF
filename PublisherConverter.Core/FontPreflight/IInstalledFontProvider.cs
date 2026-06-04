namespace PublisherConverter.Core
{
    /// <summary>
    /// Reports whether a font family is installed on the local system. The
    /// production implementation enumerates the Windows font registry; tests
    /// substitute a fake with a fixed stock list.
    ///
    /// Forward compatibility: when the automatic font-installer feature is
    /// added later, it will sit alongside this provider — the audit step
    /// still asks "is it installed?", but on a "no" the installer can attempt
    /// to fetch the font and then the cache can re-resolve. Today's surface
    /// (Normalize + IsInstalled) is what that future code will reuse.
    /// </summary>
    public interface IInstalledFontProvider
    {
        bool IsInstalled(string family);
    }
}

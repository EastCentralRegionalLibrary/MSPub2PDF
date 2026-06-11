using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FeaturesOnDemand
{
    /// <summary>
    /// Async client for the subset of the UUP Dump API the font fallback needs.
    /// Mirrors the endpoints, query parameters, JSON shape, and package-naming
    /// rules of the reference Python script (<c>CabDownloader.py</c>):
    ///
    ///   * <c>listid.php?search={build}&amp;sortByDate=1</c> — newest update UUID
    ///     for a build, filtered to the requested architecture.
    ///   * <c>get.php?id={uuid}</c> — full file manifest for that UUID.
    ///
    /// The client performs no installation and owns no Windows state, so it is
    /// fully exercisable on any platform against a fake downloader.
    /// </summary>
    public interface IUupDumpClient
    {
        /// <summary>
        /// Finds the newest update UUID matching <paramref name="buildSearch"/>
        /// whose architecture equals <paramref name="architecture"/>. Throws
        /// <see cref="UupDumpException"/> when the API returns no usable build.
        /// </summary>
        Task<string> FindLatestUpdateIdAsync(string buildSearch, string architecture, string? correlationId, CancellationToken cancellationToken);

        /// <summary>
        /// Retrieves the file manifest for <paramref name="updateId"/> as a map
        /// of file name → metadata. Throws <see cref="UupDumpException"/> when
        /// the manifest is empty or malformed.
        /// </summary>
        Task<IReadOnlyDictionary<string, UupFile>> GetFilesAsync(string updateId, string? correlationId, CancellationToken cancellationToken);
    }
}

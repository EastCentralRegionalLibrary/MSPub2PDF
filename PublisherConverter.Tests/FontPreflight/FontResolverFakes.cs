using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PublisherConverter.Core;

namespace PublisherConverter.Tests
{
    /// <summary>
    /// Test downloader. Seed responses keyed by URL — repeat hits to the
    /// same URL replay the same response. Unseeded URLs throw
    /// HttpRequestException so a strategy's "graceful failure" path is
    /// easy to exercise.
    /// </summary>
    public sealed class FakeFontDownloader : IFontDownloader
    {
        public Dictionary<string, byte[]> Responses { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        public List<string> RequestedUrls { get; } = new List<string>();
        public List<IDictionary<string, string>?> RequestedHeaders { get; } = new List<IDictionary<string, string>?>();

        public void RespondWithString(string url, string body)
            => Responses[url] = Encoding.UTF8.GetBytes(body);

        public void RespondWithBytes(string url, byte[] body) => Responses[url] = body;

        public Task DownloadAsync(string url, Stream destination, CancellationToken cancellationToken)
            => DownloadAsync(url, destination, null, cancellationToken);

        public async Task DownloadAsync(string url, Stream destination, IDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(url);
            RequestedHeaders.Add(headers);
            if (!Responses.TryGetValue(url, out var bytes))
            {
                throw new HttpRequestException($"FakeFontDownloader: no canned response for {url}");
            }
            await destination.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
        }

        public Task<string> DownloadStringAsync(string url, IDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(url);
            RequestedHeaders.Add(headers);
            if (!Responses.TryGetValue(url, out var bytes))
            {
                throw new HttpRequestException($"FakeFontDownloader: no canned response for {url}");
            }
            return Task.FromResult(Encoding.UTF8.GetString(bytes));
        }
    }

    /// <summary>
    /// Test installer. Records each install attempt; ShouldSucceed gates
    /// the return value so a strategy's "installer rejected" branch is
    /// straightforward to drive.
    /// </summary>
    public sealed class FakeUserFontInstaller : IUserFontInstaller
    {
        public List<(string family, string url, string? fileName)> InstalledFromUrl { get; } = new();
        public List<(string family, string fileName, byte[] bytes)> InstalledFromStream { get; } = new();
        public Func<string, string, bool>? StreamShouldSucceed { get; set; } // family, fileName -> ok?
        public Func<string, string, bool>? UrlShouldSucceed { get; set; }    // family, url -> ok?

        public async Task<bool> InstallFromStreamAsync(string familyName, string desiredFileName, Stream source, IList<string> log, CancellationToken cancellationToken)
        {
            using var ms = new MemoryStream();
            await source.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            InstalledFromStream.Add((familyName, desiredFileName, ms.ToArray()));
            log.Add($"[fake-installer] InstallFromStream family='{familyName}' name='{desiredFileName}' bytes={ms.Length}");
            return StreamShouldSucceed?.Invoke(familyName, desiredFileName) ?? true;
        }

        public Task<bool> InstallFromUrlAsync(string familyName, string url, string? suggestedFileName, IList<string> log, CancellationToken cancellationToken)
        {
            InstalledFromUrl.Add((familyName, url, suggestedFileName));
            log.Add($"[fake-installer] InstallFromUrl family='{familyName}' url='{url}' suggested='{suggestedFileName}'");
            return Task.FromResult(UrlShouldSucceed?.Invoke(familyName, url) ?? true);
        }
    }
}

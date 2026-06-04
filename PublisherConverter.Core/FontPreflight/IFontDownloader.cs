using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Downloads a font file by URL into the provided destination stream.
    /// Abstracted so tests can stub network I/O.
    /// </summary>
    public interface IFontDownloader
    {
        Task DownloadAsync(string url, Stream destination, CancellationToken cancellationToken);
    }

    public sealed class HttpFontDownloader : IFontDownloader
    {
        private static readonly HttpClient Shared = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient
            {
                Timeout = System.TimeSpan.FromMinutes(3),
            };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MSPub2PDF/1.0 (+font-resolver)");

            // Honor environment-supplied credentials for Google Fonts API
            // without hardcoding anything. Currently informational — the
            // download path itself works with public URLs (GitHub raw,
            // direct CDN links). A future GoogleFontsStrategy will read this
            // env var to attach the key as a query parameter.
            return c;
        }

        public async Task DownloadAsync(string url, Stream destination, CancellationToken cancellationToken)
        {
            using var response = await Shared.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await response.Content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }
}

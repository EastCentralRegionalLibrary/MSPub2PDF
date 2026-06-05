using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core
{
    /// <summary>
    /// Downloads an HTTP resource — either streaming bytes into a destination
    /// (for fonts and archives) or returning a string (for CSS / JSON
    /// metadata responses). The header overload lets strategies set a
    /// User-Agent or Accept that the upstream service requires.
    /// </summary>
    public interface IFontDownloader
    {
        Task DownloadAsync(string url, Stream destination, CancellationToken cancellationToken);

        Task DownloadAsync(string url, Stream destination, IDictionary<string, string>? headers, CancellationToken cancellationToken)
            => DownloadAsync(url, destination, cancellationToken);

        async Task<string> DownloadStringAsync(string url, IDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            using var ms = new MemoryStream();
            await DownloadAsync(url, ms, headers, cancellationToken).ConfigureAwait(false);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
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

            // GoogleFontsProvisioningStrategy and GitHubFontsProvisioningStrategy
            // remain unauthenticated and never set credentials. If a future
            // strategy needs a key (e.g. Google Fonts Developer API), it MUST
            // read it from the environment variable GOOGLE_FONTS_API_KEY at
            // call-time rather than hardcoding it here.
            return c;
        }

        public async Task DownloadAsync(string url, Stream destination, CancellationToken cancellationToken)
        {
            using var response = await Shared.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await response.Content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        public async Task DownloadAsync(string url, Stream destination, IDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyHeaders(request, headers);
            using var response = await Shared.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await response.Content.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> DownloadStringAsync(string url, IDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyHeaders(request, headers);
            using var response = await Shared.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        private static void ApplyHeaders(HttpRequestMessage request, IDictionary<string, string>? headers)
        {
            if (headers == null) return;
            foreach (var kv in headers)
            {
                if (string.Equals(kv.Key, "User-Agent", System.StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.UserAgent.Clear();
                    request.Headers.UserAgent.ParseAdd(kv.Value);
                }
                else
                {
                    request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
            }
        }
    }
}

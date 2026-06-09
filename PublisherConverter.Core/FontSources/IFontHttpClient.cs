using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PublisherConverter.Core.FontSources
{
    /// <summary>Lightweight existence/metadata probe outcome.</summary>
    public sealed class ProbeResult
    {
        public bool Exists { get; init; }
        public long? ContentLength { get; init; }
        public string? ContentType { get; init; }
        public int? StatusCode { get; init; }

        public static ProbeResult Missing { get; } = new ProbeResult { Exists = false };
    }

    /// <summary>
    /// HTTP surface for the acquisition layers: a fast HEAD/range probe, a bounded
    /// byte download, and a string GET. Every call takes an explicit timeout and a
    /// cancellation token; transient network failures and timeouts return a "miss"
    /// (no payload / Exists=false) rather than throwing, so a network error never
    /// escapes a resolver. Caller-initiated cancellation still propagates.
    /// </summary>
    public interface IFontHttpClient
    {
        Task<ProbeResult> ProbeAsync(string url, IDictionary<string, string>? headers, TimeSpan timeout, CancellationToken cancellationToken);

        Task<byte[]?> DownloadBytesAsync(string url, IDictionary<string, string>? headers, long maxBytes, TimeSpan timeout, CancellationToken cancellationToken);

        Task<string?> GetStringAsync(string url, IDictionary<string, string>? headers, TimeSpan timeout, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Default <see cref="IFontHttpClient"/> over a single reusable
    /// <see cref="HttpClient"/>. Per-call timeouts are enforced with a linked
    /// CancellationTokenSource so the shared client's own timeout is not relied on,
    /// and a timeout is reported as a miss while real cancellation propagates.
    /// </summary>
    public sealed class HttpFontClient : IFontHttpClient
    {
        private static readonly HttpClient Shared = CreateClient();

        private readonly HttpClient _client;

        public HttpFontClient(HttpClient? client = null)
        {
            _client = client ?? Shared;
        }

        private static HttpClient CreateClient()
        {
            var c = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5,
                AutomaticDecompression = DecompressionMethods.All,
            })
            {
                // Per-call timeouts govern; keep the client ceiling generous.
                Timeout = Timeout.InfiniteTimeSpan,
            };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MSPub2PDF/1.0 (+font-acquisition)");
            return c;
        }

        public async Task<ProbeResult> ProbeAsync(string url, IDictionary<string, string>? headers, TimeSpan timeout, CancellationToken cancellationToken)
        {
            // HEAD first; some hosts disallow it, so fall back to a 1-byte range GET.
            var head = await SendAsync(HttpMethod.Head, url, headers, null, timeout, cancellationToken).ConfigureAwait(false);
            if (head.faulted) return ProbeResult.Missing;
            using (head.response)
            {
                if (head.response!.IsSuccessStatusCode)
                {
                    return new ProbeResult
                    {
                        Exists = true,
                        ContentLength = head.response.Content.Headers.ContentLength,
                        ContentType = head.response.Content.Headers.ContentType?.MediaType,
                        StatusCode = (int)head.response.StatusCode,
                    };
                }
                if (head.response.StatusCode != HttpStatusCode.MethodNotAllowed &&
                    head.response.StatusCode != HttpStatusCode.NotImplemented)
                {
                    return new ProbeResult { Exists = false, StatusCode = (int)head.response.StatusCode };
                }
            }

            var rangeHeaders = new Dictionary<string, string>(headers ?? new Dictionary<string, string>()) { ["Range"] = "bytes=0-0" };
            var ranged = await SendAsync(HttpMethod.Get, url, rangeHeaders, null, timeout, cancellationToken).ConfigureAwait(false);
            if (ranged.faulted) return ProbeResult.Missing;
            using (ranged.response)
            {
                return new ProbeResult
                {
                    Exists = ranged.response!.IsSuccessStatusCode,
                    ContentLength = ranged.response.Content.Headers.ContentRange?.Length ?? ranged.response.Content.Headers.ContentLength,
                    ContentType = ranged.response.Content.Headers.ContentType?.MediaType,
                    StatusCode = (int)ranged.response.StatusCode,
                };
            }
        }

        public async Task<byte[]?> DownloadBytesAsync(string url, IDictionary<string, string>? headers, long maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var (response, faulted) = await SendAsync(HttpMethod.Get, url, headers, HttpCompletionOption.ResponseHeadersRead, timeout, cancellationToken).ConfigureAwait(false);
            if (faulted || response == null) return null;
            using (response)
            {
                if (!response.IsSuccessStatusCode) return null;
                try
                {
                    using var src = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var ms = new MemoryStream();
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (maxBytes > 0 && total > maxBytes) return null; // oversized — reject
                        ms.Write(buffer, 0, read);
                    }
                    return ms.ToArray();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    return null;
                }
            }
        }

        public async Task<string?> GetStringAsync(string url, IDictionary<string, string>? headers, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var (response, faulted) = await SendAsync(HttpMethod.Get, url, headers, HttpCompletionOption.ResponseContentRead, timeout, cancellationToken).ConfigureAwait(false);
            if (faulted || response == null) return null;
            using (response)
            {
                if (!response.IsSuccessStatusCode) return null;
                try
                {
                    return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    return null;
                }
            }
        }

        private async Task<(HttpResponseMessage? response, bool faulted)> SendAsync(
            HttpMethod method, string url, IDictionary<string, string>? headers,
            HttpCompletionOption? completion, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                var request = new HttpRequestMessage(method, url);
                ApplyHeaders(request, headers);
                var response = await _client.SendAsync(request, completion ?? HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
                return (response, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // caller cancelled — propagate
            }
            catch
            {
                // Timeout or transport failure — report as a miss.
                return (null, true);
            }
        }

        private static void ApplyHeaders(HttpRequestMessage request, IDictionary<string, string>? headers)
        {
            if (headers == null) return;
            foreach (var kv in headers)
            {
                if (string.Equals(kv.Key, "User-Agent", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.UserAgent.Clear();
                    request.Headers.UserAgent.ParseAdd(kv.Value);
                }
                else if (string.Equals(kv.Key, "Range", StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.TryAddWithoutValidation("Range", kv.Value);
                }
                else
                {
                    request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
            }
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;
using Jellyfin.Plugin.ArtworkRefresher.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArtworkRefresher.Sources;

/// <summary>Per-source counters of one run.</summary>
public sealed class SourceCounters
{
    internal int QueriesValue;
    internal int DownloadsValue;
    internal int TooManyRequestsValue;
    internal int FailuresValue;

    /// <summary>Gets the number of queries.</summary>
    public int Queries => QueriesValue;

    /// <summary>Gets the number of downloads.</summary>
    public int Downloads => DownloadsValue;

    /// <summary>Gets the number of 429 answers.</summary>
    public int TooManyRequests => TooManyRequestsValue;

    /// <summary>Gets the number of failures.</summary>
    public int Failures => FailuresValue;
}

/// <summary>
/// The only way the plugin talks to the network. Enforces https and the host allow-list on
/// every hop (redirects are followed by hand), refuses private destinations even after DNS
/// resolution, limits size, honours Retry-After and keeps credentials out of logs.
/// </summary>
public sealed class ArtworkHttpClient : IArtworkHttp, IDisposable
{
    private readonly HttpClient _client;
    private readonly Func<PluginConfiguration> _config;
    private readonly SourceRateLimiter _limiter;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, SourceCounters> _counters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtworkHttpClient"/> class.
    /// </summary>
    /// <param name="config">The configuration accessor.</param>
    /// <param name="limiter">The rate limiter.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="handler">A handler for tests, or null for the guarded default.</param>
    public ArtworkHttpClient(Func<PluginConfiguration> config, SourceRateLimiter limiter, ILogger logger, HttpMessageHandler? handler = null)
    {
        _config = config;
        _limiter = limiter;
        _logger = logger;
        _client = new HttpClient(handler ?? CreateGuardedHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var version = typeof(ArtworkHttpClient).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("ArtworkRefresher/" + version + " (+https://github.com/Minibenjy/SMCollection-Jellyfin)");
    }

    /// <summary>
    /// Gets a snapshot of the counters.
    /// </summary>
    /// <returns>Counters by source.</returns>
    public IReadOnlyDictionary<string, SourceCounters> Counters => _counters;

    /// <summary>
    /// Clears the counters, at the start of a run.
    /// </summary>
    public void ResetCounters() => _counters.Clear();

    /// <summary>
    /// Removes the query string, which may carry an API key, so an address can be logged.
    /// </summary>
    /// <param name="uri">The address.</param>
    /// <returns>Host and path only.</returns>
    public static string Redact(Uri uri) => uri.GetLeftPart(UriPartial.Path);

    /// <inheritdoc />
    public async Task<JsonDocument?> GetJsonAsync(
        string sourceId,
        Uri uri,
        int requestsPerMinute,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref For(sourceId).QueriesValue);
        var bytes = await SendAsync(sourceId, uri, requestsPerMinute, headers, 4 * 1024 * 1024, anyPublicHost: false, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            Fail(sourceId);
            _logger.LogDebug("Artwork Refresher: {Source} returned something that is not JSON ({Url})", sourceId, Redact(uri));
            return null;
        }
    }

    /// <summary>
    /// Downloads an image, with the size limit of the configuration.
    /// </summary>
    /// <param name="candidate">The candidate.</param>
    /// <param name="requestsPerMinute">The request budget of the source.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The bytes, or null on any failure.</returns>
    public async Task<byte[]?> DownloadAsync(ArtworkCandidate candidate, int requestsPerMinute, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref For(candidate.Source).DownloadsValue);
        return await SendAsync(candidate.Source, candidate.Uri, requestsPerMinute, null, _config().MaximumDownloadBytes, candidate.AnyPublicHost, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    private static SocketsHttpHandler CreateGuardedHandler()
        => new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = async (context, token) =>
            {
                // Resolve here and refuse private addresses at connection time, so a public name
                // that resolves to a private address (DNS rebinding) cannot reach the local network.
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
                foreach (var address in addresses)
                {
                    if (UrlGuard.IsPrivate(address))
                    {
                        throw new HttpRequestException("Refused: " + context.DnsEndPoint.Host + " resolves to a private address.");
                    }
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

    private SourceCounters For(string sourceId) => _counters.GetOrAdd(sourceId, _ => new SourceCounters());

    private void Fail(string sourceId) => Interlocked.Increment(ref For(sourceId).FailuresValue);

    private async Task<byte[]?> SendAsync(
        string sourceId,
        Uri start,
        int requestsPerMinute,
        IReadOnlyDictionary<string, string>? headers,
        long maxBytes,
        bool anyPublicHost,
        CancellationToken cancellationToken)
    {
        var config = _config();
        var extra = config.AllowCustomProviderHosts ? config.AdditionalAllowedHosts : [];
        var current = start;
        var attempt = 0;
        var redirects = 0;

        while (true)
        {
            if (!UrlGuard.IsAllowed(current, sourceId, anyPublicHost, extra, out var reason))
            {
                Fail(sourceId);
                _logger.LogInformation("Artwork Refresher: refused {Url} for {Source}: {Reason}", Redact(current), sourceId, reason);
                return null;
            }

            await _limiter.WaitAsync(sourceId, requestsPerMinute, cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, config.HttpTimeoutSeconds)));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                if (headers is not null)
                {
                    foreach (var (k, v) in headers)
                    {
                        request.Headers.TryAddWithoutValidation(k, v);
                    }
                }

                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (++redirects > Math.Max(0, config.MaximumRedirects) || response.Headers.Location is null)
                    {
                        Fail(sourceId);
                        return null;
                    }

                    current = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        Interlocked.Increment(ref For(sourceId).TooManyRequestsValue);
                    }

                    TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
                        ?? (response.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
                    var delay = SourceRateLimiter.BackoffFor(++attempt, retryAfter, TimeSpan.FromMinutes(5));
                    _limiter.Penalize(sourceId, delay);
                    if (attempt >= 3)
                    {
                        Fail(sourceId);
                        _logger.LogInformation("Artwork Refresher: {Source} kept answering {Status}; giving up on {Url}", sourceId, (int)response.StatusCode, Redact(current));
                        return null;
                    }

                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    // 404 simply means the provider has nothing for this id; it is not a failure of the run.
                    if ((int)response.StatusCode != 404)
                    {
                        Fail(sourceId);
                    }

                    return null;
                }

                if (response.Content.Headers.ContentLength is { } len && len > maxBytes)
                {
                    Fail(sourceId);
                    return null;
                }

                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var ms = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    ms.Write(buffer, 0, read);
                    if (ms.Length > maxBytes)
                    {
                        Fail(sourceId);
                        _logger.LogInformation("Artwork Refresher: {Url} is larger than the limit", Redact(current));
                        return null;
                    }
                }

                return ms.ToArray();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                Fail(sourceId);
                _logger.LogInformation("Artwork Refresher: request to {Url} failed: {Message}", Redact(current), ex.Message);
                return null;
            }
        }
    }
}

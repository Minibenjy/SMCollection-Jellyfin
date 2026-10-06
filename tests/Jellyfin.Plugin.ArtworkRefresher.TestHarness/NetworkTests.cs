using System.Net;
using System.Net.Http.Headers;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;
using Jellyfin.Plugin.ArtworkRefresher.Core;
using Jellyfin.Plugin.ArtworkRefresher.Sources;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArtworkRefresher.TestHarness;

internal static class NetworkTests
{
    public static async Task RunAsync()
    {
        UrlGuards();
        await RateLimiter();
        await HttpClientBehaviour();
    }

    private static void UrlGuards()
    {
        bool Allowed(string url, string source = SourceIds.Tmdb, bool any = false, string[]? extra = null)
            => UrlGuard.IsAllowed(new Uri(url), source, any, extra ?? [], out _);

        Check.True(Allowed("https://image.tmdb.org/t/p/w780/a.jpg"), "guard: https provider host");
        Check.True(!Allowed("http://image.tmdb.org/t/p/w780/a.jpg"), "guard: http refused");
        Check.True(!Allowed("https://evil.example/a.jpg"), "guard: other host refused");
        Check.True(!Allowed("https://image.tmdb.org.evil.example/a.jpg"), "guard: look-alike host refused");
        Check.True(!Allowed("https://user:pw@image.tmdb.org/a.jpg"), "guard: credentials in address refused");
        Check.True(!Allowed("https://localhost/a.jpg", any: true), "guard: localhost refused even for any host");
        Check.True(!Allowed("https://127.0.0.1/a.jpg", any: true), "guard: loopback refused");
        Check.True(!Allowed("https://10.1.2.3/a.jpg", any: true), "guard: RFC1918 refused");
        Check.True(!Allowed("https://172.20.0.1/a.jpg", any: true), "guard: 172.16/12 refused");
        Check.True(!Allowed("https://192.168.1.10/a.jpg", any: true), "guard: 192.168 refused");
        Check.True(!Allowed("https://169.254.169.254/latest/meta-data", any: true), "guard: cloud metadata address refused");
        Check.True(!Allowed("https://[::1]/a.jpg", any: true), "guard: IPv6 loopback refused");
        Check.True(!Allowed("https://[fe80::1]/a.jpg", any: true), "guard: IPv6 link-local refused");
        Check.True(!Allowed("https://[fd00::1]/a.jpg", any: true), "guard: IPv6 unique local refused");
        Check.True(!Allowed("https://[::ffff:10.0.0.1]/a.jpg", any: true), "guard: IPv4-mapped private refused");
        Check.True(!Allowed("https://printer.local/a.jpg", any: true), "guard: .local refused");
        Check.True(Allowed("https://images.example.org/a.jpg", SourceIds.GoogleCse, any: true), "guard: public host allowed for search results");
        Check.True(!Allowed("https://8.8.8.8/a.jpg"), "guard: ip literal not allowed for a provider");
        Check.True(!Allowed("https://cdn.example.net/a.jpg"), "guard: extra host not honoured when not passed");
        Check.True(Allowed("https://cdn.example.net/a.jpg", extra: ["example.net"]), "guard: extra host honoured when passed");
        Check.True(Allowed("https://upload.wikimedia.org/x.png", SourceIds.Wikimedia), "guard: wikimedia upload host");
        Check.True(Allowed("https://ia800000.us.archive.org/x.jpg", SourceIds.CoverArtArchive), "guard: archive.org subdomain for Cover Art Archive");
        Check.True(!Allowed("https://ia800000.us.archive.org/x.jpg", SourceIds.Tmdb), "guard: archive.org not allowed for TMDb");
    }

    private static async Task RateLimiter()
    {
        var now = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        var waits = new List<TimeSpan>();
        var limiter = new SourceRateLimiter(() => now, (d, _) =>
        {
            waits.Add(d);
            now += d;
            return Task.CompletedTask;
        });

        await limiter.WaitAsync("a", 60, CancellationToken.None); // first goes at once
        Check.Equal(0, waits.Count, "limiter: first request is immediate");
        await limiter.WaitAsync("a", 60, CancellationToken.None); // 60/min = one per second
        Check.Equal(1, waits.Count, "limiter: second request waits");
        Check.Equal(TimeSpan.FromSeconds(1), waits[0], "limiter: 60 per minute is one second apart");

        // Another source has its own bucket.
        waits.Clear();
        await limiter.WaitAsync("b", 60, CancellationToken.None);
        Check.Equal(0, waits.Count, "limiter: sources are isolated");

        // A penalty makes everything on that source wait, and only that source.
        limiter.Penalize("a", TimeSpan.FromSeconds(30));
        await limiter.WaitAsync("a", 600, CancellationToken.None);
        Check.True(waits.Count == 1 && waits[0] >= TimeSpan.FromSeconds(29), "limiter: penalty is honoured");
        waits.Clear();
        await limiter.WaitAsync("b", 600, CancellationToken.None);
        Check.Equal(0, waits.Count, "limiter: penalty on a does not slow b");

        // Unlimited budget still honours a penalty.
        limiter.Penalize("c", TimeSpan.FromSeconds(5));
        await limiter.WaitAsync("c", 0, CancellationToken.None);
        Check.Equal(1, waits.Count, "limiter: penalty applies without a budget");

        Check.Equal(TimeSpan.FromSeconds(2), SourceRateLimiter.BackoffFor(1, null, TimeSpan.FromMinutes(5)), "backoff: attempt 1");
        Check.Equal(TimeSpan.FromSeconds(8), SourceRateLimiter.BackoffFor(3, null, TimeSpan.FromMinutes(5)), "backoff: attempt 3");
        Check.Equal(TimeSpan.FromSeconds(120), SourceRateLimiter.BackoffFor(1, TimeSpan.FromSeconds(120), TimeSpan.FromMinutes(5)), "backoff: Retry-After wins when longer");
        Check.Equal(TimeSpan.FromMinutes(5), SourceRateLimiter.BackoffFor(1, TimeSpan.FromHours(2), TimeSpan.FromMinutes(5)), "backoff: capped");
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    private static async Task HttpClientBehaviour()
    {
        var cfg = new PluginConfiguration { MaximumDownloadBytes = 1000, HttpTimeoutSeconds = 5, MaximumRedirects = 2 };
        var handler = new FakeHandler();
        var logger = new CapturingLogger();
        var fakeNow = DateTimeOffset.UtcNow;
        var delays = new List<TimeSpan>();
        var limiter = new SourceRateLimiter(() => fakeNow, (d, _) =>
        {
            delays.Add(d);
            fakeNow += d;
            return Task.CompletedTask;
        });
        using var http = new ArtworkHttpClient(() => cfg, limiter, logger, handler);

        // JSON ok.
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"a\":1}") };
        using (var doc = await http.GetJsonAsync(SourceIds.Tmdb, new Uri("https://api.themoviedb.org/3/x?api_key=SECRETKEY"), 0, null, CancellationToken.None))
        {
            Check.True(doc is not null && doc.RootElement.GetProperty("a").GetInt32() == 1, "http: json parsed");
        }

        // A non-https or foreign address is refused without sending anything, and the key is not logged.
        handler.Requests.Clear();
        Check.True(await http.GetJsonAsync(SourceIds.Tmdb, new Uri("http://api.themoviedb.org/3/x?api_key=SECRETKEY"), 0, null, CancellationToken.None) is null, "http: http refused");
        Check.True(await http.GetJsonAsync(SourceIds.Tmdb, new Uri("https://evil.example/x?api_key=SECRETKEY"), 0, null, CancellationToken.None) is null, "http: foreign host refused");
        Check.Equal(0, handler.Requests.Count, "http: refused requests never leave");
        Check.True(logger.Lines.Count > 0 && logger.Lines.All(l => !l.Contains("SECRETKEY")), "http: API key never appears in logs");

        // Not JSON.
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") };
        Check.True(await http.GetJsonAsync(SourceIds.Tmdb, new Uri("https://api.themoviedb.org/3/x"), 0, null, CancellationToken.None) is null, "http: html answer is not json");

        // 404 is not a failure of the source.
        http.ResetCounters();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        await http.GetJsonAsync(SourceIds.Tmdb, new Uri("https://api.themoviedb.org/3/x"), 0, null, CancellationToken.None);
        Check.Equal(0, http.Counters[SourceIds.Tmdb].Failures, "http: 404 is not counted as a failure");

        // 429 with Retry-After: waits, retries, then succeeds.
        http.ResetCounters();
        delays.Clear();
        var calls = 0;
        handler.Respond = _ =>
        {
            calls++;
            if (calls == 1)
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return r;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        };
        using (var doc = await http.GetJsonAsync(SourceIds.Fanart, new Uri("https://webservice.fanart.tv/v3/movies/1"), 0, null, CancellationToken.None))
        {
            Check.True(doc is not null, "http: succeeds after a 429");
        }

        Check.Equal(2, calls, "http: retried once");
        Check.True(delays.Count == 1 && delays[0] >= TimeSpan.FromSeconds(29), "http: waited for Retry-After");
        Check.Equal(1, http.Counters[SourceIds.Fanart].TooManyRequests, "http: 429 counted");

        // Endless 429: gives up after three tries.
        calls = 0;
        handler.Respond = _ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        };
        Check.True(await http.GetJsonAsync(SourceIds.Fanart, new Uri("https://webservice.fanart.tv/v3/movies/2"), 0, null, CancellationToken.None) is null, "http: gives up on endless 429");
        Check.Equal(3, calls, "http: three attempts at most");

        // Redirects are followed by hand and every hop is checked.
        handler.Requests.Clear();
        handler.Respond = r => r.RequestUri!.Host == "image.tmdb.org"
            ? Redirect("https://192.168.1.5/internal.jpg")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) };
        var cand = Candidate("https://image.tmdb.org/t/p/w780/a.jpg", SourceIds.Tmdb);
        Check.True(await http.DownloadAsync(cand, 0, CancellationToken.None) is null, "http: redirect to a private address refused");
        Check.True(handler.Requests.All(u => u.Host == "image.tmdb.org"), "http: the private hop was never requested");

        handler.Respond = r => r.RequestUri!.Host == "image.tmdb.org"
            ? Redirect("https://evil.example/a.jpg")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) };
        Check.True(await http.DownloadAsync(cand, 0, CancellationToken.None) is null, "http: redirect to a foreign host refused");

        // A redirect inside the allow-list is followed; a loop is cut by the limit.
        handler.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/a.jpg", StringComparison.Ordinal)
            ? Redirect("https://image.tmdb.org/t/p/w780/b.jpg")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) };
        Check.True(await http.DownloadAsync(cand, 0, CancellationToken.None) is { Length: 10 }, "http: allowed redirect followed");
        handler.Respond = _ => Redirect("https://image.tmdb.org/t/p/w780/loop.jpg");
        Check.True(await http.DownloadAsync(cand, 0, CancellationToken.None) is null, "http: redirect loop cut by the limit");

        // Size limit by header and by streaming.
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[5000]) };
        Check.True(await http.DownloadAsync(cand, 0, CancellationToken.None) is null, "http: oversized download refused");
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[5000])) };
        Check.True(await http.DownloadAsync(cand, 0, CancellationToken.None) is null, "http: oversized stream refused");
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[900]) };
        Check.True(await http.DownloadAsync(cand, 0, CancellationToken.None) is { Length: 900 }, "http: download within the limit");

        // Search-engine candidates may use any public host, still https and never private.
        var google = Candidate("https://images.example.org/p.jpg", SourceIds.GoogleCse, any: true);
        Check.True(await http.DownloadAsync(google, 0, CancellationToken.None) is { Length: 900 }, "http: search result on a public host");
        var googleLocal = Candidate("https://192.168.0.2/p.jpg", SourceIds.GoogleCse, any: true);
        Check.True(await http.DownloadAsync(googleLocal, 0, CancellationToken.None) is null, "http: search result on a private address refused");

        Check.Equal("https://api.themoviedb.org/3/x", ArtworkHttpClient.Redact(new Uri("https://api.themoviedb.org/3/x?api_key=SECRET")), "redact: drops the query string");
    }

    private static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to);
        return r;
    }

    private static ArtworkCandidate Candidate(string url, string source, bool any = false)
        => new() { Source = source, Uri = new Uri(url), ImageType = ImageType.Primary, AnyPublicHost = any };
}

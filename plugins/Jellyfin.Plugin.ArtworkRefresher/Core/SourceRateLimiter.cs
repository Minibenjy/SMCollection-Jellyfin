using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ArtworkRefresher.Core;

/// <summary>
/// A per-source token bucket with a Retry-After penalty. Sources never share a bucket, so
/// a slow or blocked source does not slow the others.
/// </summary>
public sealed class SourceRateLimiter
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceRateLimiter"/> class.
    /// </summary>
    public SourceRateLimiter()
        : this(() => DateTimeOffset.UtcNow, (d, ct) => Task.Delay(d, ct))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceRateLimiter"/> class with injectable time.
    /// </summary>
    /// <param name="clock">The clock.</param>
    /// <param name="delay">The delay function.</param>
    public SourceRateLimiter(Func<DateTimeOffset> clock, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _clock = clock;
        _delay = delay;
    }

    /// <summary>
    /// Waits until one request may be sent to the source.
    /// </summary>
    /// <param name="sourceId">The source.</param>
    /// <param name="requestsPerMinute">The budget; zero or less means unlimited (a Retry-After still applies).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when a request may go.</returns>
    public async Task WaitAsync(string sourceId, int requestsPerMinute, CancellationToken cancellationToken)
    {
        var bucket = _buckets.GetOrAdd(sourceId, _ => new Bucket());
        while (true)
        {
            TimeSpan wait;
            lock (bucket)
            {
                var now = _clock();
                var interval = requestsPerMinute <= 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(1.0 / requestsPerMinute);
                var ready = bucket.NextAllowed > bucket.PenaltyUntil ? bucket.NextAllowed : bucket.PenaltyUntil;
                if (ready <= now)
                {
                    bucket.NextAllowed = now + interval;
                    return;
                }

                wait = ready - now;
            }

            await _delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records a Retry-After (or a backoff) for the source.
    /// </summary>
    /// <param name="sourceId">The source.</param>
    /// <param name="delay">How long to stay quiet.</param>
    public void Penalize(string sourceId, TimeSpan delay)
    {
        var bucket = _buckets.GetOrAdd(sourceId, _ => new Bucket());
        lock (bucket)
        {
            var until = _clock() + delay;
            if (until > bucket.PenaltyUntil)
            {
                bucket.PenaltyUntil = until;
            }
        }
    }

    /// <summary>
    /// Computes an exponential backoff delay.
    /// </summary>
    /// <param name="attempt">The attempt number, starting at 1.</param>
    /// <param name="retryAfter">The Retry-After header value, when present.</param>
    /// <param name="max">The longest delay.</param>
    /// <returns>The delay.</returns>
    public static TimeSpan BackoffFor(int attempt, TimeSpan? retryAfter, TimeSpan max)
    {
        var exp = TimeSpan.FromSeconds(Math.Pow(2, Math.Clamp(attempt, 1, 10)));
        var chosen = retryAfter is { } r && r > exp ? r : exp;
        return chosen > max ? max : chosen;
    }

    private sealed class Bucket
    {
        public DateTimeOffset NextAllowed { get; set; } = DateTimeOffset.MinValue;

        public DateTimeOffset PenaltyUntil { get; set; } = DateTimeOffset.MinValue;
    }
}

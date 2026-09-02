using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AiAssistant.Guardrails;

/// <summary>
/// Per-user hourly request ceiling.
/// </summary>
/// <remarks>
/// Assistant requests can cost real money on a metered provider, and a runaway
/// client loop can generate them faster than any human. This bounds the damage
/// (OWASP LLM10, Unbounded Consumption). It is a sliding window held in memory:
/// counters reset when the server restarts, which is an acceptable tradeoff for a
/// spend guard rather than a security control.
/// </remarks>
public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _hits = new();

    /// <summary>
    /// Records a request and reports whether it is within the configured limit.
    /// </summary>
    /// <param name="userId">The acting user.</param>
    /// <param name="limitPerHour">Maximum requests per hour; zero disables the limit.</param>
    /// <returns>Whether the request may proceed.</returns>
    public bool TryAcquire(Guid userId, int limitPerHour)
    {
        if (limitPerHour <= 0)
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddHours(-1);
        var window = _hits.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());

        lock (window)
        {
            while (window.Count > 0 && window.Peek() < cutoff)
            {
                window.Dequeue();
            }

            if (window.Count >= limitPerHour)
            {
                return false;
            }

            window.Enqueue(now);
            return true;
        }
    }
}

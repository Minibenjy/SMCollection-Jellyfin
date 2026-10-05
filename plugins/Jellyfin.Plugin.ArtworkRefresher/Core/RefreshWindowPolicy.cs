using System;
using System.Globalization;

namespace Jellyfin.Plugin.ArtworkRefresher.Core;

/// <summary>
/// Decides whether a scheduled run may start now and when it must stop. Works in a
/// time zone, so daylight saving time and windows that cross midnight behave.
/// </summary>
public static class RefreshWindowPolicy
{
    /// <summary>
    /// Parses HH:mm.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="time">The time.</param>
    /// <returns>True when valid.</returns>
    public static bool TryParseTime(string? text, out TimeOnly time)
        => TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    /// <summary>
    /// Resolves a time zone id, falling back to the local zone.
    /// </summary>
    /// <param name="id">The id, may be empty.</param>
    /// <returns>The zone.</returns>
    public static TimeZoneInfo ResolveZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Local;
    }

    /// <summary>
    /// Tells whether an instant is inside the window.
    /// </summary>
    /// <param name="nowUtc">The instant.</param>
    /// <param name="zone">The zone the window is expressed in.</param>
    /// <param name="start">The start of the window.</param>
    /// <param name="end">The end of the window.</param>
    /// <returns>True when inside. A window whose start equals its end is the whole day.</returns>
    public static bool IsInside(DateTimeOffset nowUtc, TimeZoneInfo zone, TimeOnly start, TimeOnly end)
    {
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
        if (start == end)
        {
            return true;
        }

        return start < end
            ? local >= start && local < end
            : local >= start || local < end;
    }

    /// <summary>
    /// Gets the instant the current window closes.
    /// </summary>
    /// <param name="nowUtc">An instant inside the window.</param>
    /// <param name="zone">The zone.</param>
    /// <param name="start">The start of the window.</param>
    /// <param name="end">The end of the window.</param>
    /// <returns>The closing instant, or null for an all-day window.</returns>
    public static DateTimeOffset? WindowCloses(DateTimeOffset nowUtc, TimeZoneInfo zone, TimeOnly start, TimeOnly end)
    {
        if (start == end)
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var endLocal = local.Date.Add(end.ToTimeSpan());
        if (endLocal <= local.DateTime)
        {
            endLocal = endLocal.AddDays(1);
        }

        // Skip a local time that does not exist (spring forward) by moving forward until valid.
        while (zone.IsInvalidTime(endLocal))
        {
            endLocal = endLocal.AddMinutes(1);
        }

        var offset = zone.GetUtcOffset(endLocal);
        return new DateTimeOffset(endLocal, offset).ToUniversalTime();
    }

    /// <summary>
    /// Tells whether a scheduled run is due.
    /// </summary>
    /// <param name="nowUtc">Now.</param>
    /// <param name="nextEligibleUtc">The persisted next-eligible instant, or null for never run.</param>
    /// <param name="windowEnabled">Whether a window applies.</param>
    /// <param name="zone">The zone.</param>
    /// <param name="start">The window start.</param>
    /// <param name="end">The window end.</param>
    /// <returns>True when a run should start.</returns>
    public static bool IsDue(DateTimeOffset nowUtc, DateTimeOffset? nextEligibleUtc, bool windowEnabled, TimeZoneInfo zone, TimeOnly start, TimeOnly end)
    {
        if (nextEligibleUtc is { } next && nowUtc < next)
        {
            return false;
        }

        return !windowEnabled || IsInside(nowUtc, zone, start, end);
    }

    /// <summary>
    /// Computes the deadline of a run from the window and the longest run.
    /// </summary>
    /// <param name="nowUtc">The start instant.</param>
    /// <param name="windowEnabled">Whether a window applies.</param>
    /// <param name="zone">The zone.</param>
    /// <param name="start">The window start.</param>
    /// <param name="end">The window end.</param>
    /// <param name="maxRunMinutes">The longest run, 0 for none.</param>
    /// <returns>The deadline, or null for none.</returns>
    public static DateTimeOffset? Deadline(DateTimeOffset nowUtc, bool windowEnabled, TimeZoneInfo zone, TimeOnly start, TimeOnly end, int maxRunMinutes)
    {
        DateTimeOffset? deadline = null;
        if (windowEnabled)
        {
            deadline = WindowCloses(nowUtc, zone, start, end);
        }

        if (maxRunMinutes > 0)
        {
            var cap = nowUtc.AddMinutes(maxRunMinutes);
            deadline = deadline is null || cap < deadline ? cap : deadline;
        }

        return deadline;
    }
}

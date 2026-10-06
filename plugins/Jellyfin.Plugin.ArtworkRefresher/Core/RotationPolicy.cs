using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ArtworkRefresher.Core;

/// <summary>An image of a pool: where to get it and, once downloaded, where it is cached.</summary>
public sealed class PoolEntry
{
    /// <summary>Gets or sets the stable id of the image (the id at the source, or a hash of its address).</summary>
    public string AssetId { get; set; } = string.Empty;

    /// <summary>Gets or sets the source id.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets the https address.</summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the host may be any public host.</summary>
    public bool AnyPublicHost { get; set; }

    /// <summary>Gets or sets the width, when known.</summary>
    public int? Width { get; set; }

    /// <summary>Gets or sets the height, when known.</summary>
    public int? Height { get; set; }

    /// <summary>Gets or sets the language, when known.</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets the cached file name inside the pool folder, or null when not downloaded.</summary>
    public string? CachedFile { get; set; }

    /// <summary>Gets or sets the MIME type of the cached file.</summary>
    public string? MimeType { get; set; }

    /// <summary>Gets or sets how many downloads failed in a row (an image that keeps failing is dropped from the draw).</summary>
    public int Failures { get; set; }
}

/// <summary>The result of drawing from a pool.</summary>
/// <param name="AssetId">The chosen image.</param>
/// <param name="HistoryReset">True when every image had been used and the history started again.</param>
public sealed record RotationPick(string AssetId, bool HistoryReset);

/// <summary>
/// Rotation rules that need no server: the mode of a type, the pool size, drawing without
/// repeats, and merging a freshly searched pool into the stored one.
/// </summary>
public static class RotationPolicy
{
    /// <summary>The smallest pool.</summary>
    public const int MinPool = 2;

    /// <summary>The largest pool.</summary>
    public const int MaxPool = 20;

    /// <summary>Gets the rotation mode of an image type.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="type">The image type.</param>
    /// <returns>The mode, Off when not configured.</returns>
    public static RotationMode ModeFor(PluginConfiguration configuration, ImageType type)
        => Find(configuration, type)?.Mode ?? RotationMode.Off;

    /// <summary>Gets the pool size of an image type.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="type">The image type.</param>
    /// <returns>A size between the minimum and the maximum.</returns>
    public static int PoolSizeFor(PluginConfiguration configuration, ImageType type)
    {
        var own = Find(configuration, type)?.PoolSize ?? 0;
        return Math.Clamp(own > 0 ? own : configuration.DefaultPoolSize, MinPool, MaxPool);
    }

    /// <summary>Tells whether any image type rotates.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>True when at least one type is not Off.</returns>
    public static bool AnyEnabled(PluginConfiguration configuration)
        => configuration.Rotation.Any(r => r.Mode != RotationMode.Off);

    /// <summary>Lists the image types served per page load.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The type names.</returns>
    public static string[] PerLoadTypes(PluginConfiguration configuration)
        => configuration.Rotation.Where(r => r.Mode == RotationMode.PerLoad).Select(r => r.ImageType).ToArray();

    /// <summary>
    /// Draws the next image: random among those not used since the history started, never the current
    /// one. When all were used the history starts again (keeping the current image as the first entry).
    /// </summary>
    /// <param name="pool">The ids of the images in the pool.</param>
    /// <param name="history">The ids already used; updated in place.</param>
    /// <param name="current">The image showing now, or null.</param>
    /// <param name="nextInt">Returns a number from 0 (inclusive) to the argument (exclusive); injectable for tests.</param>
    /// <returns>The pick, or null when the pool has no image different from the current one.</returns>
    public static RotationPick? Pick(IReadOnlyList<string> pool, IList<string> history, string? current, Func<int, int> nextInt)
    {
        var ids = pool.Distinct(StringComparer.Ordinal).ToList();
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(history[i]))
            {
                history.RemoveAt(i);
            }
        }

        var unused = ids.Where(id => !history.Contains(id) && !string.Equals(id, current, StringComparison.Ordinal)).ToList();
        var reset = false;
        if (unused.Count == 0)
        {
            history.Clear();
            if (current is not null && ids.Contains(current))
            {
                history.Add(current);
            }

            reset = true;
            unused = ids.Where(id => !string.Equals(id, current, StringComparison.Ordinal)).ToList();
        }

        if (unused.Count == 0)
        {
            return null;
        }

        var chosen = unused[Math.Clamp(nextInt(unused.Count), 0, unused.Count - 1)];
        history.Add(chosen);
        return new RotationPick(chosen, reset);
    }

    /// <summary>
    /// Merges freshly found candidates into a stored pool: images still on offer keep their cache and
    /// their place, new ones fill up to the size, the best-ranked first.
    /// </summary>
    /// <param name="existing">The stored pool, or null.</param>
    /// <param name="found">The candidates, best first.</param>
    /// <param name="size">The pool size.</param>
    /// <param name="current">The image showing now (kept in the pool when still on offer).</param>
    /// <returns>The new pool.</returns>
    public static List<PoolEntry> Merge(IReadOnlyList<PoolEntry>? existing, IReadOnlyList<PoolEntry> found, int size, string? current)
    {
        var stored = (existing ?? []).ToDictionary(e => e.AssetId, e => e, StringComparer.Ordinal);
        var result = new List<PoolEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(PoolEntry entry)
        {
            if (result.Count < size && seen.Add(entry.AssetId))
            {
                result.Add(stored.TryGetValue(entry.AssetId, out var old) ? old : entry);
            }
        }

        var offered = found.Where(f => !string.IsNullOrEmpty(f.AssetId)).ToList();
        if (current is not null && offered.FirstOrDefault(f => f.AssetId == current) is { } cur)
        {
            Add(cur);
        }

        foreach (var f in offered)
        {
            Add(f);
        }

        return result;
    }

    private static RotationTypeSetting? Find(PluginConfiguration configuration, ImageType type)
        => configuration.Rotation.FirstOrDefault(r => string.Equals(r.ImageType, type.ToString(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Orders items so the recently added ones come first.</summary>
public static class RecentFirstPolicy
{
    /// <summary>
    /// Puts items created after the cutoff first (newest first), then the others in their order.
    /// </summary>
    /// <typeparam name="T">The item type.</typeparam>
    /// <param name="items">The items.</param>
    /// <param name="created">Gets the creation time of an item.</param>
    /// <param name="cutoff">The oldest creation time that still counts as recent.</param>
    /// <returns>The recent items and the rest.</returns>
    public static (List<T> Recent, List<T> Others) Split<T>(IEnumerable<T> items, Func<T, DateTime> created, DateTime cutoff)
    {
        var recent = new List<T>();
        var rest = new List<T>();
        foreach (var item in items)
        {
            (created(item) >= cutoff ? recent : rest).Add(item);
        }

        return (recent.OrderByDescending(created).ToList(), rest);
    }
}

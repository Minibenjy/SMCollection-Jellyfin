using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.ArtworkRefresher.Core;

/// <summary>
/// Items that were just added to the library and wait a little before they are filled (Jellyfin does its
/// own refresh first). An item added again moves to the back, so a series still being scanned is not
/// touched half way. The queue is bounded: the oldest entry is dropped when it is full.
/// </summary>
public sealed class NewItemQueue
{
    private readonly Dictionary<Guid, DateTimeOffset> _added = [];
    private readonly Lock _sync = new();
    private readonly int _capacity;

    /// <summary>Initializes a new instance of the <see cref="NewItemQueue"/> class.</summary>
    /// <param name="capacity">The most entries kept.</param>
    public NewItemQueue(int capacity = 500)
    {
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>Gets the number of waiting items.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _added.Count;
            }
        }
    }

    /// <summary>Adds an item, or moves it to the back.</summary>
    /// <param name="id">The item id.</param>
    /// <param name="now">The time it was seen.</param>
    public void Add(Guid id, DateTimeOffset now)
    {
        lock (_sync)
        {
            _added[id] = now;
            if (_added.Count > _capacity)
            {
                var oldest = _added.OrderBy(kv => kv.Value).First().Key;
                _added.Remove(oldest);
            }
        }
    }

    /// <summary>Gets the items that waited long enough, oldest first.</summary>
    /// <param name="now">The time now.</param>
    /// <param name="delay">How long an item waits.</param>
    /// <param name="max">The most items returned.</param>
    /// <returns>The ids.</returns>
    public IReadOnlyList<Guid> Due(DateTimeOffset now, TimeSpan delay, int max)
    {
        lock (_sync)
        {
            return _added.Where(kv => now - kv.Value >= delay).OrderBy(kv => kv.Value).Take(Math.Max(1, max)).Select(kv => kv.Key).ToList();
        }
    }

    /// <summary>Removes items that were handled.</summary>
    /// <param name="ids">The ids.</param>
    public void Remove(IEnumerable<Guid> ids)
    {
        lock (_sync)
        {
            foreach (var id in ids)
            {
                _added.Remove(id);
            }
        }
    }
}

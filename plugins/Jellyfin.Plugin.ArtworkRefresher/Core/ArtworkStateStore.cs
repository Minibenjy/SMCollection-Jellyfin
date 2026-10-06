using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ArtworkRefresher.Core;

/// <summary>State of one image slot of one item.</summary>
public sealed class SlotState
{
    /// <summary>Gets or sets the last attempt.</summary>
    public DateTimeOffset? LastAttemptUtc { get; set; }

    /// <summary>Gets or sets the last success.</summary>
    public DateTimeOffset? LastSuccessUtc { get; set; }

    /// <summary>Gets or sets the source of the last saved image.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the id of the image at the source.</summary>
    public string? SourceAssetId { get; set; }

    /// <summary>Gets or sets a hash of the saved content.</summary>
    public string? ContentHash { get; set; }

    /// <summary>Gets or sets the mosaic signature.</summary>
    public string? MosaicSignature { get; set; }

    /// <summary>Gets or sets consecutive failures.</summary>
    public int FailureCount { get; set; }

    /// <summary>Gets or sets when to try again.</summary>
    public DateTimeOffset? NextRetryUtc { get; set; }

    /// <summary>Gets or sets, for the metadata record of an item, the fields this plugin filled (field name, short hash of the value written).</summary>
    public Dictionary<string, string>? FilledFields { get; set; }
}

/// <summary>The rotation pool of one image slot. Kept in its own file because it can be large.</summary>
public sealed class PoolState
{
    /// <summary>Gets or sets the candidate images.</summary>
    public List<PoolEntry> Entries { get; set; } = [];

    /// <summary>Gets or sets when the pool was last searched.</summary>
    public DateTimeOffset? BuiltUtc { get; set; }

    /// <summary>Gets or sets the pool images already used since the history started.</summary>
    public List<string> History { get; set; } = [];

    /// <summary>Gets or sets the local calendar day (yyyy-MM-dd) of the last daily rotation.</summary>
    public string? LastRotationDay { get; set; }

    /// <summary>Gets or sets how many times the image of this slot really changed by rotation.</summary>
    public int RotationChanges { get; set; }

    /// <summary>Gets or sets the file name (in the originals folder) of the image that was there before the first rotation.</summary>
    public string? OriginalBackup { get; set; }

    /// <summary>Gets or sets a value indicating whether the image was changed by somebody else after the last rotation (rotation of this slot is paused).</summary>
    public bool ExternalChange { get; set; }

    /// <summary>Gets or sets the hash of the last image the rotation wrote.</summary>
    public string? WrittenHash { get; set; }
}

/// <summary>Scheduler state.</summary>
public sealed class SchedulerState
{
    /// <summary>Gets or sets when the last run started.</summary>
    public DateTimeOffset? LastRunStartedUtc { get; set; }

    /// <summary>Gets or sets when the last run completed.</summary>
    public DateTimeOffset? LastRunCompletedUtc { get; set; }

    /// <summary>Gets or sets when the next run may start.</summary>
    public DateTimeOffset? NextEligibleRunUtc { get; set; }

    /// <summary>Gets or sets the last status.</summary>
    public string? LastRunStatus { get; set; }

    /// <summary>Gets or sets how many images really changed by daily rotation in the last run.</summary>
    public int LastRunRotationChanges { get; set; }

    /// <summary>Gets or sets how many metadata fields the last run filled, by field.</summary>
    public Dictionary<string, int> LastRunMetadataFilled { get; set; } = [];

    /// <summary>Gets or sets how many times a per-load image was served since the plugin started keeping count.</summary>
    public long PerLoadServes { get; set; }
}

/// <summary>State of a generated Home image.</summary>
public sealed class HomeImageState
{
    /// <summary>Gets or sets the content hash (the ETag).</summary>
    public string? ContentHash { get; set; }

    /// <summary>Gets or sets the signature of the source items.</summary>
    public string? SourceItemsSignature { get; set; }

    /// <summary>Gets or sets when it was generated.</summary>
    public DateTimeOffset? GeneratedUtc { get; set; }

    /// <summary>Gets or sets the file name inside the plugin data folder.</summary>
    public string? FileName { get; set; }
}

/// <summary>The whole state document.</summary>
public sealed class ArtworkStateDocument
{
    /// <summary>Gets or sets the schema version.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Gets or sets the scheduler state.</summary>
    public SchedulerState Scheduler { get; set; } = new();

    /// <summary>Gets or sets the per-item state, by item id then slot key.</summary>
    public Dictionary<Guid, Dictionary<string, SlotState>> Items { get; set; } = [];

    /// <summary>Gets or sets the rotation pools by item then slot key. Saved in a separate file.</summary>
    [JsonIgnore]
    public Dictionary<Guid, Dictionary<string, PoolState>> Pools { get; set; } = [];

    /// <summary>Gets or sets the secret that signs rotating image addresses (created on first use).</summary>
    public string? SigningKey { get; set; }

    /// <summary>Gets or sets generated Home images, by visibility profile and section key.</summary>
    public Dictionary<string, HomeImageState> Home { get; set; } = [];

    /// <summary>Gets or sets the last manual refresh per user and item, for the cooldown.</summary>
    public Dictionary<string, DateTimeOffset> UserRefreshes { get; set; } = [];
}

/// <summary>
/// Keeps the state in one JSON file in the plugin data folder. Writes go to a temporary file and
/// replace the old one atomically, so a crash never leaves a half-written state.
/// </summary>
public sealed class ArtworkStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly TimeSpan PoolSaveInterval = TimeSpan.FromMinutes(2);

    private readonly string _path;
    private readonly Lock _sync = new();
    private readonly Lock _fileSync = new();
    private ArtworkStateDocument? _document;
    private bool _poolsDirty;
    private DateTimeOffset _poolsSaved = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtworkStateStore"/> class.
    /// </summary>
    /// <param name="path">The state file path.</param>
    public ArtworkStateStore(string path)
    {
        _path = path;
    }

    /// <summary>Gets the state file path.</summary>
    public string Path => _path;

    /// <summary>
    /// Reads or mutates the document under a lock.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="action">The action.</param>
    /// <returns>The action's result.</returns>
    public T Use<T>(Func<ArtworkStateDocument, T> action)
    {
        lock (_sync)
        {
            return action(Load());
        }
    }

    /// <summary>
    /// Mutates the document under a lock.
    /// </summary>
    /// <param name="action">The action.</param>
    public void Update(Action<ArtworkStateDocument> action)
        => Use(d =>
        {
            action(d);
            return 0;
        });

    /// <summary>
    /// Gets a slot state, creating it when absent.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="slotKey">The slot key.</param>
    /// <param name="change">What to do with it.</param>
    public void Touch(Guid itemId, string slotKey, Action<SlotState> change)
        => Update(d =>
        {
            if (!d.Items.TryGetValue(itemId, out var slots))
            {
                slots = [];
                d.Items[itemId] = slots;
            }

            if (!slots.TryGetValue(slotKey, out var state))
            {
                state = new SlotState();
                slots[slotKey] = state;
            }

            change(state);
        });

    /// <summary>Gets the pool of a slot, or null.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="slotKey">The slot key.</param>
    /// <returns>The pool state (live object: read it, change it only through <see cref="TouchPool"/>).</returns>
    public PoolState? GetPool(Guid itemId, string slotKey)
        => Use(d => d.Pools.TryGetValue(itemId, out var slots) && slots.TryGetValue(slotKey, out var s) ? s : null);

    /// <summary>Changes the pool of a slot, creating it when absent.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="slotKey">The slot key.</param>
    /// <param name="change">What to do with it.</param>
    public void TouchPool(Guid itemId, string slotKey, Action<PoolState> change)
        => Update(d =>
        {
            if (!d.Pools.TryGetValue(itemId, out var slots))
            {
                slots = [];
                d.Pools[itemId] = slots;
            }

            if (!slots.TryGetValue(slotKey, out var state))
            {
                state = new PoolState();
                slots[slotKey] = state;
            }

            change(state);
            _poolsDirty = true;
        });

    /// <summary>Marks the pools as changed (after a change made through <see cref="Update"/>).</summary>
    public void MarkPoolsDirty()
    {
        lock (_sync)
        {
            _poolsDirty = true;
        }
    }

    /// <summary>
    /// Gets a copy-free read of a slot.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="slotKey">The slot key.</param>
    /// <returns>The state, or null.</returns>
    public SlotState? Get(Guid itemId, string slotKey)
        => Use(d => d.Items.TryGetValue(itemId, out var slots) && slots.TryGetValue(slotKey, out var s) ? s : null);

    /// <summary>
    /// Writes the document to disk. The pools file (large) is written only when it changed and either
    /// <paramref name="force"/> is set or a couple of minutes passed since its last write.
    /// </summary>
    /// <param name="force">True to write the pools file now if it changed.</param>
    /// <returns>A task.</returns>
    public Task SaveAsync(bool force = false)
    {
        string json;
        string? poolsJson = null;
        lock (_sync)
        {
            var doc = Load();
            json = JsonSerializer.Serialize(doc, Options);
            if (_poolsDirty && (force || DateTimeOffset.UtcNow - _poolsSaved >= PoolSaveInterval))
            {
                poolsJson = JsonSerializer.Serialize(doc.Pools, Options);
                _poolsDirty = false;
                _poolsSaved = DateTimeOffset.UtcNow;
            }
        }

        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Two saves at once (a run and a page-load draw) must not fight over the temporary file.
        lock (_fileSync)
        {
            WriteAtomic(_path, json);
            if (poolsJson is not null)
            {
                WriteAtomic(PoolsPath, poolsJson);
            }
        }

        return Task.CompletedTask;
    }

    private string PoolsPath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_path) ?? string.Empty, "pools.json");

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        if (File.Exists(path))
        {
            File.Replace(temp, path, null);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    private ArtworkStateDocument Load()
    {
        if (_document is not null)
        {
            return _document;
        }

        try
        {
            if (File.Exists(_path))
            {
                _document = JsonSerializer.Deserialize<ArtworkStateDocument>(File.ReadAllText(_path), Options);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A damaged state file only means the plugin forgets what it did; start clean.
            _document = null;
        }

        _document ??= new ArtworkStateDocument();
        try
        {
            if (File.Exists(PoolsPath))
            {
                _document.Pools = JsonSerializer.Deserialize<Dictionary<Guid, Dictionary<string, PoolState>>>(File.ReadAllText(PoolsPath), Options) ?? [];
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Pools are only candidates: losing them means they are searched again.
            _document.Pools = [];
        }

        return _document;
    }
}

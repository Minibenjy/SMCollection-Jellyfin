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

    private readonly string _path;
    private readonly Lock _sync = new();
    private ArtworkStateDocument? _document;

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

    /// <summary>
    /// Gets a copy-free read of a slot.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="slotKey">The slot key.</param>
    /// <returns>The state, or null.</returns>
    public SlotState? Get(Guid itemId, string slotKey)
        => Use(d => d.Items.TryGetValue(itemId, out var slots) && slots.TryGetValue(slotKey, out var s) ? s : null);

    /// <summary>
    /// Writes the document to disk.
    /// </summary>
    /// <returns>A task.</returns>
    public Task SaveAsync()
    {
        string json;
        lock (_sync)
        {
            json = JsonSerializer.Serialize(Load(), Options);
        }

        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(_path))
        {
            File.Replace(temp, _path, null);
        }
        else
        {
            File.Move(temp, _path);
        }

        return Task.CompletedTask;
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
        return _document;
    }
}

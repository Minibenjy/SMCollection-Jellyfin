using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EnhancedPdfReader.Progress;

/// <summary>
/// Stores per-user reading positions on the server, in a JSON file inside the plugin data folder.
/// </summary>
/// <remarks>
/// Positions are kept per user id so that two accounts using the same browser do not share
/// a bookmark, and so that the position follows the account across devices.
/// </remarks>
public class ProgressStore
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private readonly ILogger<ProgressStore> _logger;
    private readonly object _lock = new();

    private Dictionary<string, Dictionary<string, ReadingPosition>>? _data;
    private string? _path;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgressStore"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{ProgressStore}"/> interface.</param>
    public ProgressStore(ILogger<ProgressStore> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the saved position of a user in a document, or null when there is none.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <returns>The stored position, or null.</returns>
    public ReadingPosition? Get(Guid userId, Guid itemId)
    {
        lock (_lock)
        {
            Load();
            if (_data!.TryGetValue(Key(userId), out var byItem) && byItem.TryGetValue(Key(itemId), out var pos))
            {
                return pos;
            }

            return null;
        }
    }

    /// <summary>
    /// Saves the position of a user in a document. A page of zero or less clears it.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="numPages">The total page count reported by the client.</param>
    /// <returns>The stored position.</returns>
    public ReadingPosition Set(Guid userId, Guid itemId, int page, int numPages)
    {
        var pos = new ReadingPosition
        {
            Page = page > 0 ? page : 0,
            NumPages = numPages > 0 ? numPages : 0,
            UpdatedUtc = DateTime.UtcNow
        };

        lock (_lock)
        {
            Load();
            var userKey = Key(userId);
            if (!_data!.TryGetValue(userKey, out var byItem))
            {
                byItem = new Dictionary<string, ReadingPosition>(StringComparer.OrdinalIgnoreCase);
                _data[userKey] = byItem;
            }

            if (pos.Page == 0)
            {
                byItem.Remove(Key(itemId));
            }
            else
            {
                byItem[Key(itemId)] = pos;
            }

            Save();
        }

        return pos;
    }

    private static string Key(Guid id) => id.ToString("N");

    private string Path
    {
        get
        {
            if (_path is null)
            {
                var folder = Plugin.Instance?.DataFolderPath
                    ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EnhancedPdfReader");
                Directory.CreateDirectory(folder);
                _path = System.IO.Path.Combine(folder, "progress.json");
            }

            return _path;
        }
    }

    private void Load()
    {
        if (_data is not null)
        {
            return;
        }

        try
        {
            if (File.Exists(Path))
            {
                var json = File.ReadAllText(Path);
                _data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, ReadingPosition>>>(json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EnhancedPdfReader] could not read {Path}, starting with an empty store", Path);
        }

        _data ??= new Dictionary<string, Dictionary<string, ReadingPosition>>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        try
        {
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, _jsonOptions));
            File.Move(tmp, Path, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EnhancedPdfReader] could not write {Path}", Path);
        }
    }
}

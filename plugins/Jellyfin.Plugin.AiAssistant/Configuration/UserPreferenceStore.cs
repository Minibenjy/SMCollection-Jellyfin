using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.AiAssistant.Configuration;

/// <summary>
/// File-backed store for per-user assistant settings.
/// </summary>
public sealed class UserPreferenceStore : IUserPreferenceStore, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _path;

    private Dictionary<string, UserPreferences>? _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserPreferenceStore"/> class.
    /// </summary>
    /// <param name="paths">Server application paths.</param>
    public UserPreferenceStore(IServerApplicationPaths paths)
    {
        var dir = Path.Combine(paths.DataPath, "ai-assistant");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "user-preferences.json");
    }

    /// <inheritdoc />
    public async Task<UserPreferences?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return all.GetValueOrDefault(userId.ToString("N"));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(Guid userId, UserPreferences preferences, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await LoadAsync(cancellationToken).ConfigureAwait(false);
            all[userId.ToString("N")] = preferences;

            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(all), cancellationToken).ConfigureAwait(false);
            File.Move(tmp, _path, overwrite: true);
            _cache = all;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    private async Task<Dictionary<string, UserPreferences>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        if (!File.Exists(_path))
        {
            return _cache = new Dictionary<string, UserPreferences>(StringComparer.Ordinal);
        }

        var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
        return _cache = JsonSerializer.Deserialize<Dictionary<string, UserPreferences>>(json)
                        ?? new Dictionary<string, UserPreferences>(StringComparer.Ordinal);
    }
}

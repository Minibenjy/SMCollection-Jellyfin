using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.AiAssistant.Configuration;

/// <summary>
/// Resolves which language a user's library metadata is written in.
/// </summary>
/// <remarks>
/// The administrator sets a server-wide default; a user may override it for their
/// own conversations. Resolution is a hot path — it runs on every exchange — so the
/// per-user value is cached and invalidated when settings are saved.
/// </remarks>
public sealed class MetadataLanguageResolver
{
    private readonly IUserPreferenceStore _preferences;
    private readonly ConcurrentDictionary<Guid, string> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataLanguageResolver"/> class.
    /// </summary>
    /// <param name="preferences">Per-user settings.</param>
    public MetadataLanguageResolver(IUserPreferenceStore preferences)
    {
        _preferences = preferences;
    }

    /// <summary>
    /// Gets the effective metadata language for a user.
    /// </summary>
    /// <param name="userId">The acting user.</param>
    /// <returns>The language name, or an empty string when unset.</returns>
    public string Resolve(Guid userId)
    {
        // Only the user's own override is cached. Folding the server default in here
        // would mean an administrator changing it had no effect until every cached
        // user expired — which, with no expiry, means until the next restart.
        if (!_cache.TryGetValue(userId, out var own))
        {
            var stored = _preferences.GetAsync(userId, default).GetAwaiter().GetResult();
            own = stored?.MetadataLanguage ?? string.Empty;
            _cache[userId] = own;
        }

        return string.IsNullOrWhiteSpace(own)
            ? Plugin.Config.MetadataLanguage ?? string.Empty
            : own;
    }

    /// <summary>
    /// Drops a user's cached value after their settings change.
    /// </summary>
    /// <param name="userId">The user whose settings changed.</param>
    public void Invalidate(Guid userId) => _cache.TryRemove(userId, out _);

    /// <summary>Drops every cached override.</summary>
    public void InvalidateAll() => _cache.Clear();
}

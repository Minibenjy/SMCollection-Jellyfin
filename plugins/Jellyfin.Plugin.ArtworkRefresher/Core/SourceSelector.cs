using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;

namespace Jellyfin.Plugin.ArtworkRefresher.Core;

/// <summary>
/// Picks the sources to ask, and in which order, for an item kind. Pure: depends only on the
/// configuration, so it is tested without a server.
/// </summary>
public static class SourceSelector
{
    private static readonly Dictionary<BaseItemKind, string[]> DefaultOrder = new()
    {
        [BaseItemKind.Movie] = [SourceIds.Tmdb, SourceIds.Fanart, SourceIds.Wikimedia],
        [BaseItemKind.Series] = [SourceIds.Tmdb, SourceIds.Fanart, SourceIds.Wikimedia],
        [BaseItemKind.Season] = [SourceIds.Tmdb, SourceIds.Fanart],
        [BaseItemKind.Episode] = [SourceIds.Tmdb],
        [BaseItemKind.MusicAlbum] = [SourceIds.CoverArtArchive, SourceIds.Fanart],
        [BaseItemKind.MusicArtist] = [SourceIds.Fanart, SourceIds.Wikimedia],
        [BaseItemKind.Person] = [SourceIds.Tmdb, SourceIds.Wikimedia],
        [BaseItemKind.Genre] = [SourceIds.LocalMosaic],
        [BaseItemKind.MusicGenre] = [SourceIds.LocalMosaic],
        [BaseItemKind.Studio] = [SourceIds.Tmdb, SourceIds.Wikimedia, SourceIds.LocalMosaic],
        [BaseItemKind.BoxSet] = [SourceIds.Tmdb, SourceIds.LocalMosaic],
        [BaseItemKind.Book] = [SourceIds.OpenLibrary],
    };

    /// <summary>
    /// Gets the built-in order for a kind.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The source ids.</returns>
    public static IReadOnlyList<string> BuiltInOrder(BaseItemKind kind)
        => DefaultOrder.TryGetValue(kind, out var order) ? order : [];

    /// <summary>
    /// Gets whether a source can be used with the current configuration.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="sourceId">The source id.</param>
    /// <returns>True when enabled and, if it needs credentials, configured.</returns>
    public static bool IsUsable(PluginConfiguration configuration, string sourceId)
    {
        var s = configuration.Sources;
        return sourceId switch
        {
            SourceIds.Tmdb => s.Tmdb.Enabled && !string.IsNullOrWhiteSpace(s.Tmdb.ApiKey),
            SourceIds.Fanart => s.Fanart.Enabled && !string.IsNullOrWhiteSpace(s.Fanart.ApiKey),
            SourceIds.Wikimedia => s.Wikimedia.Enabled,
            SourceIds.OpenLibrary => s.OpenLibrary.Enabled,
            SourceIds.CoverArtArchive => s.CoverArtArchive.Enabled,
            SourceIds.GoogleCse => s.GoogleCse.Enabled
                                   && !string.IsNullOrWhiteSpace(s.GoogleCse.ApiKey)
                                   && !string.IsNullOrWhiteSpace(s.GoogleCse.ApiKey2),
            SourceIds.LocalMosaic => configuration.MosaicEnabled,
            _ => false
        };
    }

    /// <summary>
    /// Selects the ordered, usable sources for a kind. Google is always the last resort for
    /// kinds that opt in through their order, never added implicitly.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="kind">The item kind.</param>
    /// <returns>The ordered source ids.</returns>
    public static IReadOnlyList<string> Select(PluginConfiguration configuration, BaseItemKind kind)
    {
        var custom = configuration.SourceOrder.FirstOrDefault(
            e => string.Equals(e.ItemKind, kind.ToString(), StringComparison.OrdinalIgnoreCase));
        var order = custom is { Sources.Length: > 0 } ? custom.Sources : BuiltInOrder(kind).ToArray();

        // Search engines go last whatever the order says.
        return order
            .Where(id => IsUsable(configuration, id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => string.Equals(id, SourceIds.GoogleCse, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ToList();
    }
}

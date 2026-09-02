using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AiAssistant.Guardrails;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// Shared shaping of library items into the payloads tools return.
/// </summary>
/// <remarks>
/// Every tool projects through here so the model sees one vocabulary. When each tool
/// invented its own field names, the model had to relearn the shape per tool and
/// mixed them up — passing an episode's <c>id</c> where a <c>series_id</c> was wanted
/// was the recurring symptom.
///
/// The projection is an allow-list, not a redaction pass. File paths, provider ids
/// and internal database columns never enter the payload, so there is nothing in it
/// for the model to leak (OWASP LLM02).
/// </remarks>
internal static class ItemProjection
{
    /// <summary>
    /// The item kinds a tool may be asked about, and the only values accepted in a
    /// <c>kinds</c> argument.
    /// </summary>
    public static readonly IReadOnlyList<string> SelectableKinds = new[]
    {
        "Movie", "Series", "Season", "Episode", "Book", "AudioBook",
        "MusicAlbum", "Audio", "MusicVideo", "Video", "Playlist", "BoxSet"
    };

    /// <summary>
    /// Kinds that can actually sit in a playlist. Anything else is either a container
    /// to expand or an outright mistake.
    /// </summary>
    public static readonly IReadOnlyList<BaseItemKind> PlayableKinds = new[]
    {
        BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video,
        BaseItemKind.Audio, BaseItemKind.MusicVideo, BaseItemKind.AudioBook
    };

    /// <summary>
    /// Parses a <c>kinds</c> argument.
    /// </summary>
    /// <remarks>
    /// An unknown kind used to be dropped silently, which turned a narrowing filter
    /// into no filter at all: a request for <c>kinds=["Playlist"]</c> — not in the
    /// old enum — became a title search across the whole library, came back empty,
    /// and the model concluded the playlist did not exist and made a duplicate. A
    /// filter that cannot be honoured has to fail loudly.
    /// </remarks>
    /// <param name="names">Raw kind names from the model.</param>
    /// <param name="kinds">The parsed kinds.</param>
    /// <param name="error">Set when one of the names is not a selectable kind.</param>
    /// <returns>True when every name parsed.</returns>
    public static bool TryParseKinds(
        IReadOnlyList<string> names,
        out BaseItemKind[] kinds,
        out string? error)
    {
        var parsed = new List<BaseItemKind>();

        foreach (var name in names)
        {
            var match = SelectableKinds.FirstOrDefault(
                k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

            if (match is null || !Enum.TryParse<BaseItemKind>(match, out var kind))
            {
                kinds = Array.Empty<BaseItemKind>();
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"\"{UntrustedContent.Sanitize(name)}\" is not a valid kind. Valid kinds are: {string.Join(", ", SelectableKinds)}. Call again with one of those.");
                return false;
            }

            parsed.Add(kind);
        }

        kinds = parsed.ToArray();
        error = null;
        return true;
    }

    /// <summary>
    /// Projects an item onto the minimum the model needs to act on it.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The projection.</returns>
    public static JsonObject Project(BaseItem item)
    {
        var projected = new JsonObject
        {
            ["id"] = item.Id.ToString("N"),
            ["name"] = UntrustedContent.Sanitize(item.Name),
            ["type"] = item.GetBaseItemKind().ToString(),
            ["year"] = item.ProductionYear,
            ["overview"] = UntrustedContent.Sanitize(Truncate(item.Overview, 400)),
            ["genres"] = new JsonArray(item.Genres.Take(5).Select(g => (JsonNode)UntrustedContent.Sanitize(g)).ToArray())
        };

        if (item.RunTimeTicks > 0)
        {
            projected["runtime_minutes"] = (int)TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalMinutes;
        }

        // An episode result carries its series id, because the next thing the model
        // wants is list_episodes and it was otherwise passing the episode's own id.
        if (item is Episode episode)
        {
            projected["season"] = episode.ParentIndexNumber;
            projected["episode"] = episode.IndexNumber;
            projected["series_name"] = UntrustedContent.Sanitize(episode.SeriesName);

            if (episode.SeriesId != Guid.Empty)
            {
                projected["series_id"] = episode.SeriesId.ToString("N");
            }
        }

        if (item is Season season)
        {
            projected["season"] = season.IndexNumber;
            projected["series_name"] = UntrustedContent.Sanitize(season.SeriesName);

            if (season.SeriesId != Guid.Empty)
            {
                projected["series_id"] = season.SeriesId.ToString("N");
            }
        }

        if (item is MusicAlbum album)
        {
            projected["album_artist"] = UntrustedContent.Sanitize(string.Join(", ", album.AlbumArtists.Take(3)));
        }

        return projected;
    }

    /// <summary>
    /// Projects an item and adds this user's own playback state.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="scope">The acting user.</param>
    /// <param name="userData">User data manager.</param>
    /// <returns>The projection.</returns>
    public static JsonObject ProjectWithState(BaseItem item, UserScope scope, IUserDataManager userData)
    {
        var projected = Project(item);
        var data = userData.GetUserData(scope.User, item);

        if (data is not null)
        {
            projected["watched"] = data.Played;
            projected["favorite"] = data.IsFavorite;

            if (data.PlaybackPositionTicks > 0 && item.RunTimeTicks is > 0)
            {
                projected["resume_percent"] =
                    (int)Math.Round(100d * data.PlaybackPositionTicks / item.RunTimeTicks.Value);
            }
        }

        return projected;
    }

    /// <summary>
    /// Projects a list of items.
    /// </summary>
    /// <param name="items">The items.</param>
    /// <returns>The array.</returns>
    public static JsonArray ProjectAll(IEnumerable<BaseItem> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(Project(item));
        }

        return array;
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];
}

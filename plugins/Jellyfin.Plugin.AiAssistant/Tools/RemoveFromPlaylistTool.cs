using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiAssistant.Guardrails;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// Removes items from one of the user's playlists.
/// </summary>
public sealed class RemoveFromPlaylistTool : IAssistantTool
{
    private readonly IPlaylistManager _playlistManager;
    private readonly PlaylistAccess _playlists;
    private readonly ItemResolver _resolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="RemoveFromPlaylistTool"/> class.
    /// </summary>
    /// <param name="playlistManager">Playlist manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    public RemoveFromPlaylistTool(IPlaylistManager playlistManager, ILibraryManager libraryManager)
    {
        _playlistManager = playlistManager;
        _playlists = new PlaylistAccess(playlistManager);
        _resolver = new ItemResolver(libraryManager);
    }

    /// <inheritdoc />
    public string Name => "remove_from_playlist";

    /// <inheritdoc />
    public string Description =>
        "Remove items from one of this user's playlists. Read it with get_playlist first so "
        + "you remove the ids that are actually in it.";

    /// <inheritdoc />
    public bool IsMutating => true;

    /// <inheritdoc />
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["playlist"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The playlist's name, or its id from list_playlists."
            },
            ["item_ids"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Ids of items to remove, as returned by get_playlist.",
                ["items"] = new JsonObject { ["type"] = "string" }
            }
        },
        ["required"] = new JsonArray("playlist", "item_ids"),
        ["additionalProperties"] = false
    };

    /// <inheritdoc />
    public string DescribeCall(UserScope scope, JsonObject arguments)
    {
        var playlist = ToolArguments.GetString(arguments, "playlist") ?? "a playlist";
        var count = ToolArguments.GetStringList(arguments, "item_ids").Count;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Remove {count} item(s) from the playlist \"{UntrustedContent.Sanitize(playlist)}\"?");
    }

    /// <inheritdoc />
    public async Task<JsonNode> ExecuteAsync(UserScope scope, JsonObject arguments, CancellationToken cancellationToken)
    {
        var reference = (ToolArguments.GetString(arguments, "playlist") ?? string.Empty).Trim();
        var playlist = _playlists.Find(scope, reference);

        if (playlist is null)
        {
            return new JsonObject { ["error"] = _playlists.NotFoundMessage(scope) };
        }

        var requested = ToolArguments.GetStringList(arguments, "item_ids");
        if (requested.Count == 0)
        {
            return new JsonObject { ["error"] = "At least one item id is required." };
        }

        // Removal is by the ids the playlist actually holds, so a name or a stale id
        // resolves to the right entry rather than silently removing nothing.
        var present = playlist.GetManageableItems()
            .Select(entry => entry.Item2)
            .Where(item => item is not null)
            .ToList();
        var targets = new System.Collections.Generic.List<Guid>();
        var missing = new JsonArray();

        foreach (var raw in requested)
        {
            var resolved = _resolver.Resolve(scope, raw);
            var match = resolved is not null
                ? present.FirstOrDefault(p => p.Id == resolved.Id)
                : present.FirstOrDefault(p => p.Name.Contains(raw, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                missing.Add(UntrustedContent.Sanitize(raw));
                continue;
            }

            targets.Add(match.Id);
        }

        if (targets.Count == 0)
        {
            return new JsonObject
            {
                ["error"] = "None of those items are in that playlist. Nothing was removed.",
                ["not_in_playlist"] = missing
            };
        }

        await _playlistManager.RemoveItemFromPlaylistAsync(
                playlist.Id.ToString("N"),
                targets.Select(id => id.ToString("N")))
            .ConfigureAwait(false);

        var payload = new JsonObject
        {
            ["removed"] = targets.Count,
            ["playlist_id"] = playlist.Id.ToString("N"),
            ["name"] = UntrustedContent.Sanitize(playlist.Name)
        };

        if (missing.Count > 0)
        {
            payload["not_in_playlist"] = missing;
            payload["you_must_tell_the_user"] =
                "These were not in the playlist and were not removed. Say so.";
        }

        return payload;
    }
}

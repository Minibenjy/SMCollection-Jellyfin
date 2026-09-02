using System.Collections.Generic;
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
/// Adds items to a playlist the user already has.
/// </summary>
/// <remarks>
/// The missing half of playlist support. Without it "add this to that playlist" had
/// no tool behind it, and the model reached for create_playlist instead — producing
/// duplicates rather than an edit.
/// </remarks>
public sealed class AddToPlaylistTool : IAssistantTool
{
    private const int MaxItems = 200;

    private readonly IPlaylistManager _playlistManager;
    private readonly PlaylistAccess _playlists;
    private readonly ItemResolver _resolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="AddToPlaylistTool"/> class.
    /// </summary>
    /// <param name="playlistManager">Playlist manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    public AddToPlaylistTool(IPlaylistManager playlistManager, ILibraryManager libraryManager)
    {
        _playlistManager = playlistManager;
        _playlists = new PlaylistAccess(playlistManager);
        _resolver = new ItemResolver(libraryManager);
    }

    /// <inheritdoc />
    public string Name => "add_to_playlist";

    /// <inheritdoc />
    public string Description =>
        "Add items to a playlist this user already has, named by id or by name. "
        + "This is how you extend a playlist — never create a second one with the same name. "
        + "A whole series, season or album may be passed instead of its episodes; it is expanded.";

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
                ["description"] = "Ids of items to add, as returned by a lookup tool.",
                ["items"] = new JsonObject { ["type"] = "string" }
            }
        },
        ["required"] = new JsonArray("playlist", "item_ids"),
        ["additionalProperties"] = false
    };

    /// <remarks>
    /// Resolved the same way ExecuteAsync resolves it, for the same reason as
    /// CreatePlaylistTool.DescribeCall: a container id in item_ids expands, and the
    /// number the person approves has to be the number that actually lands.
    /// </remarks>
    /// <inheritdoc />
    public string DescribeCall(UserScope scope, JsonObject arguments)
    {
        var playlist = ToolArguments.GetString(arguments, "playlist") ?? "a playlist";
        var requested = ToolArguments.GetStringList(arguments, "item_ids");
        var resolution = _resolver.ResolveForPlaylist(scope, requested, MaxItems);

        var detail = resolution.Expanded.Count > 0
            ? $" ({string.Join("; ", resolution.Expanded)})"
            : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Add {resolution.Items.Count} item(s){detail} to the playlist \"{UntrustedContent.Sanitize(playlist)}\"?");
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

        var resolution = _resolver.ResolveForPlaylist(scope, requested, MaxItems);

        if (resolution.Items.Count == 0)
        {
            return new JsonObject
            {
                ["error"] = "None of those references resolved to something that can go in a playlist. "
                            + "The playlist was not changed.",
                ["rejected"] = new JsonArray(resolution.Rejected.Select(r => (JsonNode)r).ToArray())
            };
        }

        // Jellyfin itself silently drops anything already in the playlist rather than
        // erroring, which is the right behaviour for the playlist but the wrong one for
        // what the model is told: observed live, a random draw that happened to repeat
        // three earlier episodes was reported as "15 added" when only 12 landed.
        // Filtering here means the count in the result is the count that actually
        // changed, and the repeats are named rather than silently absorbed.
        var existingIds = new HashSet<System.Guid>(
            playlist.GetManageableItems().Select(entry => entry.Item2).Where(i => i is not null).Select(i => i!.Id));

        var toAdd = resolution.Items.Where(i => !existingIds.Contains(i.Id)).ToList();
        var alreadyPresent = resolution.Items.Where(i => existingIds.Contains(i.Id)).ToList();

        if (toAdd.Count > 0)
        {
            await _playlistManager.AddItemToPlaylistAsync(
                    playlist.Id,
                    toAdd.Select(i => i.Id).ToArray(),
                    scope.UserId)
                .ConfigureAwait(false);
        }

        var payload = PlaylistReport.Build(
            new JsonObject
            {
                ["added"] = toAdd.Count > 0,
                ["playlist_id"] = playlist.Id.ToString("N"),
                ["name"] = UntrustedContent.Sanitize(playlist.Name)
            },
            resolution with { Items = toAdd });

        if (alreadyPresent.Count > 0)
        {
            payload["already_in_playlist"] = new JsonArray(alreadyPresent
                .Select(i => (JsonNode)UntrustedContent.Sanitize(i.Name))
                .ToArray());
            payload["you_must_tell_the_user"] = "Some items were already in this playlist and were "
                                                + "not added again. The item_count above excludes them "
                                                + "— say the real number that changed, not the number "
                                                + "you were originally asked to add.";
        }

        return payload;
    }
}

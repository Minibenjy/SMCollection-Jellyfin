using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiAssistant.Guardrails;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// Renames or deletes one of the user's playlists.
/// </summary>
/// <remarks>
/// Renaming and deleting share a tool because they share the only risky part: naming
/// exactly which playlist is about to change, in a sentence the person reads before
/// approving. Deletion is the one action here that destroys something, so the
/// confirmation line says the playlist's name and how much is in it.
/// </remarks>
public sealed class ManagePlaylistTool : IAssistantTool
{
    private readonly IPlaylistManager _playlistManager;
    private readonly ILibraryManager _libraryManager;
    private readonly PlaylistAccess _playlists;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagePlaylistTool"/> class.
    /// </summary>
    /// <param name="playlistManager">Playlist manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    public ManagePlaylistTool(IPlaylistManager playlistManager, ILibraryManager libraryManager)
    {
        _playlistManager = playlistManager;
        _libraryManager = libraryManager;
        _playlists = new PlaylistAccess(playlistManager);
    }

    /// <inheritdoc />
    public string Name => "manage_playlist";

    /// <inheritdoc />
    public string Description =>
        "Rename or delete one of this user's playlists. Deleting is permanent — only do it when "
        + "the user has clearly asked for it. Use list_playlists first to get the right one.";

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
            ["action"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "What to do with it.",
                ["enum"] = new JsonArray("rename", "delete")
            },
            ["new_name"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The new name. Required when action is \"rename\"."
            }
        },
        ["required"] = new JsonArray("playlist", "action"),
        ["additionalProperties"] = false
    };

    /// <inheritdoc />
    public string DescribeCall(UserScope scope, JsonObject arguments)
    {
        var reference = (ToolArguments.GetString(arguments, "playlist") ?? string.Empty).Trim();
        var found = _playlists.Find(scope, reference);
        var playlist = UntrustedContent.Sanitize(found?.Name ?? reference);
        var action = (ToolArguments.GetString(arguments, "action") ?? string.Empty).Trim();

        if (action.Equals("delete", System.StringComparison.OrdinalIgnoreCase))
        {
            var size = found?.LinkedChildren.Length;
            var detail = size is > 0 ? $" ({size} item(s))" : string.Empty;
            return string.Create(CultureInfo.InvariantCulture, $"Permanently delete the playlist \"{playlist}\"{detail}?");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Rename the playlist \"{playlist}\" to \"{UntrustedContent.Sanitize(ToolArguments.GetString(arguments, "new_name") ?? string.Empty)}\"?");
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

        var action = (ToolArguments.GetString(arguments, "action") ?? string.Empty).Trim();

        if (action.Equals("delete", System.StringComparison.OrdinalIgnoreCase))
        {
            var name = playlist.Name;

            // A playlist is metadata, not media: deleting it must never reach the files
            // its entries point at.
            _libraryManager.DeleteItem(
                playlist,
                new DeleteOptions { DeleteFileLocation = true },
                notifyParentItem: true);

            return new JsonObject
            {
                ["deleted"] = true,
                ["name"] = UntrustedContent.Sanitize(name),
                ["note"] = "The playlist is gone. The media it pointed at was not touched."
            };
        }

        if (!action.Equals("rename", System.StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject { ["error"] = "action must be \"rename\" or \"delete\"." };
        }

        var newName = (ToolArguments.GetString(arguments, "new_name") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            return new JsonObject { ["error"] = "new_name is required when renaming." };
        }

        if (_playlists.All(scope).Any(p => p.Id != playlist.Id
                && string.Equals(p.Name, newName, System.StringComparison.OrdinalIgnoreCase)))
        {
            return new JsonObject
            {
                ["error"] = "This user already has a different playlist with that name. Nothing was renamed."
            };
        }

        await _playlistManager.UpdatePlaylist(new PlaylistUpdateRequest
        {
            Id = playlist.Id,
            UserId = scope.UserId,
            Name = newName
        }).ConfigureAwait(false);

        return new JsonObject
        {
            ["renamed"] = true,
            ["playlist_id"] = playlist.Id.ToString("N"),
            ["name"] = UntrustedContent.Sanitize(newName)
        };
    }
}

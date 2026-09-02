using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiAssistant.Guardrails;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// Reads what is inside one of the user's playlists.
/// </summary>
public sealed class GetPlaylistTool : IAssistantTool
{
    private const int MaxItems = 60;

    private readonly PlaylistAccess _playlists;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="GetPlaylistTool"/> class.
    /// </summary>
    /// <param name="playlistManager">Playlist manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    public GetPlaylistTool(IPlaylistManager playlistManager, ILibraryManager libraryManager)
    {
        _playlists = new PlaylistAccess(playlistManager);
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public string Name => "get_playlist";

    /// <inheritdoc />
    public string Description =>
        "Read the contents of one of this user's playlists, by id or by name. "
        + "Use it before changing a playlist, and to answer \"what is in my X playlist\".";

    /// <inheritdoc />
    public bool IsMutating => false;

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
            }
        },
        ["required"] = new JsonArray("playlist"),
        ["additionalProperties"] = false
    };

    /// <inheritdoc />
    public Task<JsonNode> ExecuteAsync(UserScope scope, JsonObject arguments, CancellationToken cancellationToken)
    {
        var reference = (ToolArguments.GetString(arguments, "playlist") ?? string.Empty).Trim();
        var playlist = _playlists.Find(scope, reference);

        if (playlist is null)
        {
            return Task.FromResult<JsonNode>(new JsonObject
            {
                ["error"] = _playlists.NotFoundMessage(scope)
            });
        }

        // GetManageableItems pairs each playlist entry with the item it points at, so
        // an entry whose item has since left the library resolves to null rather than
        // silently shortening the list.
        var children = playlist.GetManageableItems()
            .Select(entry => entry.Item2)
            .Where(item => item is not null && item.IsVisible(scope.User))
            .ToList();

        return Task.FromResult<JsonNode>(new JsonObject
        {
            ["id"] = playlist.Id.ToString("N"),
            ["name"] = UntrustedContent.Sanitize(playlist.Name),
            ["total"] = children.Count,
            ["items"] = ItemProjection.ProjectAll(children.Take(MaxItems))
        });
    }
}

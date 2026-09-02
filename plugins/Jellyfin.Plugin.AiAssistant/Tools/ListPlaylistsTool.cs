using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiAssistant.Guardrails;
using MediaBrowser.Controller.Playlists;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// Lists the playlists the acting user owns.
/// </summary>
/// <remarks>
/// The assistant could create playlists long before it could see them, which is how
/// it ended up making three with the same name: asked whether one already existed it
/// had only search_library, which does not find playlists by name, so it concluded
/// there was none.
/// </remarks>
public sealed class ListPlaylistsTool : IAssistantTool
{
    private readonly PlaylistAccess _playlists;

    /// <summary>
    /// Initializes a new instance of the <see cref="ListPlaylistsTool"/> class.
    /// </summary>
    /// <param name="playlistManager">Playlist manager.</param>
    public ListPlaylistsTool(IPlaylistManager playlistManager)
    {
        _playlists = new PlaylistAccess(playlistManager);
    }

    /// <inheritdoc />
    public string Name => "list_playlists";

    /// <inheritdoc />
    public string Description =>
        "List this user's playlists with their ids and sizes. "
        + "Call this before creating a playlist, so you add to an existing one instead of "
        + "making a duplicate, and whenever the user refers to a playlist by name.";

    /// <inheritdoc />
    public bool IsMutating => false;

    /// <inheritdoc />
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
        ["required"] = new JsonArray(),
        ["additionalProperties"] = false
    };

    /// <inheritdoc />
    public Task<JsonNode> ExecuteAsync(UserScope scope, JsonObject arguments, CancellationToken cancellationToken)
    {
        var items = new JsonArray();

        foreach (var playlist in _playlists.All(scope))
        {
            items.Add(new JsonObject
            {
                ["id"] = playlist.Id.ToString("N"),
                ["name"] = UntrustedContent.Sanitize(playlist.Name),
                ["item_count"] = playlist.LinkedChildren.Length,
                ["media_type"] = playlist.MediaType.ToString()
            });
        }

        return Task.FromResult<JsonNode>(new JsonObject
        {
            ["total"] = items.Count,
            ["playlists"] = items
        });
    }
}

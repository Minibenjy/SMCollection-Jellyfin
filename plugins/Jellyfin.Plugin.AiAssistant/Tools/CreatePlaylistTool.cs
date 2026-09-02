using System;
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
/// Creates a playlist owned by the acting user.
/// </summary>
/// <remarks>
/// The first tool here that changes anything, so it is the first subject to
/// confirmation: the model proposes, the person approves, and only then does this
/// run. See <see cref="IAssistantTool.IsMutating"/>.
/// </remarks>
public sealed class CreatePlaylistTool : IAssistantTool
{
    private const int MaxItems = 200;

    private readonly IPlaylistManager _playlistManager;
    private readonly PlaylistAccess _playlists;
    private readonly ItemResolver _resolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="CreatePlaylistTool"/> class.
    /// </summary>
    /// <param name="playlistManager">Playlist manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    public CreatePlaylistTool(IPlaylistManager playlistManager, ILibraryManager libraryManager)
    {
        _playlistManager = playlistManager;
        _playlists = new PlaylistAccess(playlistManager);
        _resolver = new ItemResolver(libraryManager);
    }

    /// <inheritdoc />
    public string Name => "create_playlist";

    /// <inheritdoc />
    public string Description =>
        "Create a NEW playlist in this user's account from items you have already looked up. "
        + "Pass the item ids exactly as a tool returned them — never invent one, and never pass "
        + "a playlist_id from an earlier result. "
        + "A whole series, season or album may be passed instead of its episodes; it is expanded. "
        + "To put items into a playlist that already exists, use add_to_playlist — do not create "
        + "a second playlist with the same name.";

    /// <inheritdoc />
    public bool IsMutating => true;

    /// <inheritdoc />
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["name"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Name for the new playlist."
            },
            ["item_ids"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Ids of items to add, as returned by a lookup tool.",
                ["items"] = new JsonObject { ["type"] = "string" }
            }
        },
        ["required"] = new JsonArray("name", "item_ids"),
        ["additionalProperties"] = false
    };

    /// <remarks>
    /// This resolves the references the same way ExecuteAsync will, so the number the
    /// person approves is the number that actually lands. Describing the raw argument
    /// count instead was the bug: asked for a random assortment, the model picked a
    /// whole series as one of five "items", the confirmation read "5 item(s)", and what
    /// was actually created had 159 — the series expanded to every episode it has. The
    /// approval step exists specifically to catch a surprise like that before it
    /// happens; reporting the pre-expansion count defeated the entire point of it.
    /// </remarks>
    /// <inheritdoc />
    public string DescribeCall(UserScope scope, JsonObject arguments)
    {
        var name = ToolArguments.GetString(arguments, "name") ?? "Untitled";
        var requested = ToolArguments.GetStringList(arguments, "item_ids");
        var resolution = _resolver.ResolveForPlaylist(scope, requested, MaxItems);

        var detail = resolution.Expanded.Count > 0
            ? $" ({string.Join("; ", resolution.Expanded)})"
            : string.Empty;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Create a playlist called \"{UntrustedContent.Sanitize(name)}\" with {resolution.Items.Count} item(s){detail}?");
    }

    /// <inheritdoc />
    public async Task<JsonNode> ExecuteAsync(UserScope scope, JsonObject arguments, CancellationToken cancellationToken)
    {
        var name = (ToolArguments.GetString(arguments, "name") ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return new JsonObject { ["error"] = "A playlist name is required." };
        }

        var requested = ToolArguments.GetStringList(arguments, "item_ids");
        if (requested.Count == 0)
        {
            return new JsonObject { ["error"] = "At least one item id is required." };
        }

        // Refusing the duplicate is the point. Left to itself the model answers "add
        // Medabots to that playlist" by calling the only writing tool it has, which
        // created a second playlist of the same name — three times over, in one
        // session, each a clone of the last.
        var existing = _playlists.All(scope)
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            return new JsonObject
            {
                ["error"] = string.Create(
                    CultureInfo.InvariantCulture,
                    $"This user already has a playlist called \"{UntrustedContent.Sanitize(existing.Name)}\". Nothing was created."),
                ["existing_playlist_id"] = existing.Id.ToString("N"),
                ["what_to_do"] = "If the user wants these items in that playlist, call "
                                 + "add_to_playlist with this id. If they genuinely want a "
                                 + "separate one, ask them for a different name first."
            };
        }

        // Re-resolve every reference against the acting user. The model supplies these,
        // so they are untrusted input: an id it hallucinated, an id belonging to an item
        // this user cannot see, or the id of a playlist it created a moment ago, must
        // not end up silently in their new playlist.
        var resolution = _resolver.ResolveForPlaylist(scope, requested, MaxItems);

        if (resolution.Items.Count == 0)
        {
            return new JsonObject
            {
                ["error"] = "None of those references resolved to something that can go in a playlist. "
                            + "Nothing was created.",
                ["rejected"] = new JsonArray(resolution.Rejected.Select(r => (JsonNode)r).ToArray())
            };
        }

        var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
        {
            Name = name,
            ItemIdList = resolution.Items.Select(i => i.Id).ToArray(),
            UserId = scope.UserId
        }).ConfigureAwait(false);

        return PlaylistReport.Build(
            new JsonObject
            {
                ["created"] = true,
                ["playlist_id"] = result.Id,
                ["name"] = UntrustedContent.Sanitize(name)
            },
            resolution);
    }
}

using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiAssistant.Guardrails;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// Adds or removes items from the acting user's favourites.
/// </summary>
public sealed class SetFavoriteTool : IAssistantTool
{
    private readonly ItemResolver _resolver;
    private readonly IUserDataManager _userData;

    /// <summary>
    /// Initializes a new instance of the <see cref="SetFavoriteTool"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userData">User data manager.</param>
    public SetFavoriteTool(ILibraryManager libraryManager, IUserDataManager userData)
    {
        _userData = userData;
        _resolver = new ItemResolver(libraryManager);
    }

    /// <inheritdoc />
    public string Name => "set_favorite";

    /// <inheritdoc />
    public string Description =>
        "Add items to this user's favourites, or take them out. "
        + "Films, series and albums can be given by name; an EPISODE must be given by id from "
        + "list_episodes. "
        + "Favourites are per-user; this changes nothing for anyone else.";

    /// <inheritdoc />
    public bool IsMutating => true;

    /// <inheritdoc />
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["items"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Names or ids of what to mark.",
                ["items"] = new JsonObject { ["type"] = "string" }
            },
            ["favorite"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "True to add to favourites, false to remove."
            }
        },
        ["required"] = new JsonArray("items", "favorite"),
        ["additionalProperties"] = false
    };

    /// <inheritdoc />
    public string DescribeCall(UserScope scope, JsonObject arguments)
    {
        var names = ToolArguments.GetStringList(arguments, "items");
        var favorite = ToolArguments.GetBool(arguments, "favorite", true);
        var what = UntrustedContent.Sanitize(string.Join(", ", names.Take(3)))
                   + (names.Count > 3 ? "…" : string.Empty);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(favorite ? "Add" : "Remove")} {what} {(favorite ? "to" : "from")} your favourites?");
    }

    /// <inheritdoc />
    public Task<JsonNode> ExecuteAsync(UserScope scope, JsonObject arguments, CancellationToken cancellationToken)
    {
        var references = ToolArguments.GetStringList(arguments, "items");
        if (references.Count == 0)
        {
            return Task.FromResult<JsonNode>(new JsonObject { ["error"] = "At least one item is required." });
        }

        var favorite = ToolArguments.GetBool(arguments, "favorite", true);

        var changed = new JsonArray();
        var notFound = new JsonArray();

        foreach (var reference in references)
        {
            var item = _resolver.Resolve(scope, reference);
            if (item is null)
            {
                notFound.Add(UntrustedContent.Sanitize(reference));
                continue;
            }

            var data = _userData.GetUserData(scope.User, item);
            if (data is null)
            {
                notFound.Add(UntrustedContent.Sanitize(reference));
                continue;
            }

            data.IsFavorite = favorite;
            _userData.SaveUserData(scope.User, item, data, UserDataSaveReason.UpdateUserRating, CancellationToken.None);

            changed.Add(UntrustedContent.Sanitize(item.Name));
        }

        var payload = new JsonObject
        {
            ["favorite"] = favorite,
            ["changed"] = changed
        };

        if (notFound.Count > 0)
        {
            payload["not_found"] = notFound;
            payload["you_must_tell_the_user"] = "These were not found and were not changed. Say so.";
            payload["hint"] = "A compound name like \"Show - Episode 13\" never resolves. For an "
                              + "episode, call list_episodes for the series and pass the episode's id.";
        }

        return Task.FromResult<JsonNode>(payload);
    }
}

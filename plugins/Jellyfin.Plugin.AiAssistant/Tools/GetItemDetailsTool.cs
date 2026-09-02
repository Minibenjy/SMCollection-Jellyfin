using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AiAssistant.Guardrails;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// Reads everything the library knows about one item.
/// </summary>
/// <remarks>
/// The counterweight to answering from training data. When somebody asks what a show
/// is about, or how long a film runs, or whether they have finished it, the honest
/// answer is whatever this library recorded — which is frequently a translation, an
/// edit, or nothing at all. Reading it is cheaper than remembering it and is the only
/// version that is actually true of their copy.
/// </remarks>
public sealed class GetItemDetailsTool : IAssistantTool
{
    private readonly ItemResolver _resolver;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userData;

    /// <summary>
    /// Initializes a new instance of the <see cref="GetItemDetailsTool"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userData">User data manager.</param>
    public GetItemDetailsTool(ILibraryManager libraryManager, IUserDataManager userData)
    {
        _libraryManager = libraryManager;
        _userData = userData;
        _resolver = new ItemResolver(libraryManager);
    }

    /// <inheritdoc />
    public string Name => "get_item_details";

    /// <inheritdoc />
    public string Description =>
        "Read the full library record for one title: overview, genres, studios, rating, "
        + "runtime, cast, and whether this user has watched it. Accepts a name or an id. "
        + "Prefer this over answering about a title from memory — the library's own metadata "
        + "is what the user actually has.";

    /// <inheritdoc />
    public bool IsMutating => false;

    /// <inheritdoc />
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["item"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The title, or an id returned by another tool."
            }
        },
        ["required"] = new JsonArray("item"),
        ["additionalProperties"] = false
    };

    /// <inheritdoc />
    public Task<JsonNode> ExecuteAsync(UserScope scope, JsonObject arguments, CancellationToken cancellationToken)
    {
        var reference = (ToolArguments.GetString(arguments, "item") ?? string.Empty).Trim();
        if (reference.Length == 0)
        {
            return Task.FromResult<JsonNode>(new JsonObject { ["error"] = "An item name or id is required." });
        }

        var item = _resolver.Resolve(scope, reference);
        if (item is null)
        {
            return Task.FromResult<JsonNode>(new JsonObject
            {
                ["error"] = "Nothing in this user's library matches that name or id.",
                ["hint"] = "Try search_library with a shorter fragment of the title, "
                           + "written as the library would spell it."
            });
        }

        var payload = ItemProjection.ProjectWithState(item, scope, _userData);

        payload["studios"] = new JsonArray(item.Studios.Take(5)
            .Select(s => (JsonNode)UntrustedContent.Sanitize(s)).ToArray());
        payload["tags"] = new JsonArray(item.Tags.Take(10)
            .Select(t => (JsonNode)UntrustedContent.Sanitize(t)).ToArray());

        if (!string.IsNullOrWhiteSpace(item.OfficialRating))
        {
            payload["official_rating"] = UntrustedContent.Sanitize(item.OfficialRating);
        }

        if (item.CommunityRating is not null)
        {
            payload["community_rating"] = item.CommunityRating;
        }

        var people = _libraryManager.GetPeople(item)
            .Where(p => p.Type is PersonKind.Actor or PersonKind.Director or PersonKind.Writer)
            .Take(10)
            .Select(p => (JsonNode)UntrustedContent.Sanitize($"{p.Name} ({p.Type})"))
            .ToArray();

        if (people.Length > 0)
        {
            payload["people"] = new JsonArray(people);
        }

        if (item is Series series)
        {
            var seasons = _libraryManager.GetItemsResult(new InternalItemsQuery(scope.User)
            {
                IncludeItemTypes = new[] { BaseItemKind.Season },
                ParentId = series.Id,
                Recursive = true,
                Limit = 60
            });

            payload["season_count"] = seasons.TotalRecordCount;
            payload["next_step"] = "Call list_episodes with this id to see the real episode names.";
        }

        return Task.FromResult<JsonNode>(payload);
    }
}

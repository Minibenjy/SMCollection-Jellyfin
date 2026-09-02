using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Querying;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// Reports what this user has half-finished and what they should watch next.
/// </summary>
/// <remarks>
/// The assistant was told in its instructions that it could recall where somebody
/// left off, and had no tool that could do it — so it answered the question from the
/// conversation or not at all. This is that tool.
/// </remarks>
public sealed class ContinueWatchingTool : IAssistantTool
{
    private const int MaxLimit = 20;

    private readonly ILibraryManager _libraryManager;
    private readonly ITVSeriesManager _tvSeries;
    private readonly IUserDataManager _userData;

    /// <summary>
    /// Initializes a new instance of the <see cref="ContinueWatchingTool"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="tvSeries">TV series manager.</param>
    /// <param name="userData">User data manager.</param>
    public ContinueWatchingTool(
        ILibraryManager libraryManager,
        ITVSeriesManager tvSeries,
        IUserDataManager userData)
    {
        _libraryManager = libraryManager;
        _tvSeries = tvSeries;
        _userData = userData;
    }

    /// <inheritdoc />
    public string Name => "continue_watching";

    /// <inheritdoc />
    public string Description =>
        "What this user has started and not finished, and the next unwatched episode of the "
        + "series they are partway through. Use this for \"where did I leave off\", \"what "
        + "should I watch next\" and \"carry on with what I was watching\".";

    /// <inheritdoc />
    public bool IsMutating => false;

    /// <inheritdoc />
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["include"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Which list to return. Default \"both\".",
                ["enum"] = new JsonArray("both", "resume", "next_up")
            },
            ["limit"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Maximum items per list (1-20).",
                ["minimum"] = 1,
                ["maximum"] = MaxLimit
            }
        },
        ["required"] = new JsonArray(),
        ["additionalProperties"] = false
    };

    /// <inheritdoc />
    public Task<JsonNode> ExecuteAsync(UserScope scope, JsonObject arguments, CancellationToken cancellationToken)
    {
        var limit = System.Math.Clamp(ToolArguments.GetInt(arguments, "limit", 8), 1, MaxLimit);
        var include = (ToolArguments.GetString(arguments, "include") ?? "both").Trim().ToLowerInvariant();

        var payload = new JsonObject();

        if (include is "both" or "resume")
        {
            var resumable = _libraryManager.GetItemsResult(new InternalItemsQuery(scope.User)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video },
                Recursive = true,
                IsResumable = true,
                Limit = limit,
                OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) }
            });

            payload["resume"] = new JsonArray(resumable.Items
                .Select(i => (JsonNode)ItemProjection.ProjectWithState(i, scope, _userData))
                .ToArray());
        }

        if (include is "both" or "next_up")
        {
            var nextUp = _tvSeries.GetNextUp(
                new NextUpQuery
                {
                    User = scope.User,
                    Limit = limit
                },
                new DtoOptions(false));

            payload["next_up"] = new JsonArray(nextUp.Items
                .Select(i => (JsonNode)ItemProjection.Project(i))
                .ToArray());
        }

        payload["note"] = "resume is what is part-played. next_up is the next unwatched episode of "
                          + "series already begun. Both are this user's own state.";

        return Task.FromResult<JsonNode>(payload);
    }
}

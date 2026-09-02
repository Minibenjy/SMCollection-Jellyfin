using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiAssistant.Guardrails;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// Lists the real episodes of a series.
/// </summary>
/// <remarks>
/// Without this, a question about an episode leaves the model nothing to do but
/// recall the show from training and search for a title it remembers. That fails
/// silently and confidently: an English episode name searched against a library
/// catalogued in Spanish matches nothing, and the user is told the episode is
/// absent. Enumerating what is actually there removes the need to remember at all.
/// </remarks>
public sealed class ListEpisodesTool : IAssistantTool
{
    private const int MaxLimit = 60;

    private readonly ItemResolver _resolver;
    private readonly EpisodeQuery _episodes;

    /// <summary>
    /// Initializes a new instance of the <see cref="ListEpisodesTool"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    public ListEpisodesTool(ILibraryManager libraryManager)
    {
        _resolver = new ItemResolver(libraryManager);
        _episodes = new EpisodeQuery(libraryManager);
    }

    /// <inheritdoc />
    public string Name => "list_episodes";

    /// <inheritdoc />
    public string Description =>
        "List the episodes one series actually has in this library, with their real names, "
        + "season and episode numbers. Accepts the series name directly — you do not need to "
        + "search for its id first. Always use this before saying anything about a specific "
        + "episode: never answer about an episode from your own knowledge of the show, and "
        + "never search for an episode title you remember. "
        + "For episodes from several series at once, use pick_episodes instead.";

    /// <inheritdoc />
    public bool IsMutating => false;

    /// <inheritdoc />
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["series_id"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "The series' name, or its id as returned by search_library."
            },
            ["season"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Optional season number to restrict the listing to."
            },
            ["random"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Draw the episodes at random instead of in broadcast order."
            },
            ["unwatched_only"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Only episodes this user has not finished."
            },
            ["limit"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Maximum episodes to return (1-60).",
                ["minimum"] = 1,
                ["maximum"] = MaxLimit
            }
        },
        ["required"] = new JsonArray("series_id"),
        ["additionalProperties"] = false
    };

    /// <inheritdoc />
    public Task<JsonNode> ExecuteAsync(UserScope scope, JsonObject arguments, CancellationToken cancellationToken)
    {
        var raw = (ToolArguments.GetString(arguments, "series_id") ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return Task.FromResult<JsonNode>(new JsonObject
            {
                ["error"] = "A series id or name is required."
            });
        }

        // A name is accepted as well as an id because models reliably pass the name —
        // observed live, repeatedly. Rejecting it bought a wasted round trip and, with
        // a smaller model, sometimes a give-up. Looking it up here costs one query.
        var series = _resolver.ResolveSeries(scope, raw);
        if (series is null)
        {
            return Task.FromResult<JsonNode>(new JsonObject
            {
                ["error"] = "No series matching that id or name is in this user's library.",
                ["hint"] = "Call search_library with kinds=[\"Series\"] and no query to see "
                           + "which series this library actually has, then use one of those names."
            });
        }

        var limit = Math.Clamp(ToolArguments.GetInt(arguments, "limit", 40), 1, MaxLimit);
        var season = ToolArguments.GetInt(arguments, "season", 0);
        var random = ToolArguments.GetBool(arguments, "random", false);
        var unwatchedOnly = ToolArguments.GetBool(arguments, "unwatched_only", false);

        var (episodes, total) = _episodes.Read(scope, series, season, limit, random, unwatchedOnly);

        var payload = new JsonObject
        {
            ["series"] = UntrustedContent.Sanitize(series.Name),
            ["series_id"] = series.Id.ToString("N"),
            ["total"] = total,
            ["returned"] = episodes.Count,
            ["episodes"] = EpisodeQuery.Project(episodes)
        };

        if (total > episodes.Count)
        {
            payload["note"] = "More episodes exist than were returned. Ask for a specific season "
                              + "if you need the rest.";
        }

        if (episodes.Count == 0)
        {
            payload["hint"] = "This series has no episodes in the library for that filter.";
        }

        return Task.FromResult<JsonNode>(payload);
    }
}

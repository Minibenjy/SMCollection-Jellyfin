using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AiAssistant.Guardrails;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// Picks episodes from several series at once.
/// </summary>
/// <remarks>
/// This exists because of a specific, reproducible failure. Asked for "3 random
/// episodes each of Bobobo, Mirmo and Medabots", the model had no way to say that in
/// one call, so it issued a title search per series against episode names. Two shows
/// have real episode titles and matched; the third stores its episodes as "Episodio
/// 1", "Episodio 2" and matched nothing — so the playlist was built silently without
/// it, and nothing in the exchange said a series had been dropped.
///
/// One call per request removes both halves of that: the lookup is by series, which
/// is the thing that has a findable name, and a series that yields nothing is
/// reported by name instead of vanishing.
///
/// <para>
/// A second, related failure showed up once series names were no longer required to
/// be looked up first: asked for a random mix with no series named, one exchange
/// invented ten famous shows from training data — Breaking Bad, The Mandalorian —
/// none of which this library has, wasting a call before it recovered by listing the
/// library's real series itself. A second exchange called this tool with an empty
/// <c>series</c> array, got the validation error, listed the library's series, and
/// then stopped and asked the person to name one instead of finishing the job.
/// <c>series_count</c> is the fix: when nobody named specific series, the model asks
/// for N random ones from this library instead of guessing at global ones or giving
/// up, and the whole request is one call again.
/// </para>
/// </remarks>
public sealed class PickEpisodesTool : IAssistantTool
{
    private const int MaxSeries = 10;
    private const int MaxPerSeries = 20;

    private readonly ILibraryManager _libraryManager;
    private readonly ItemResolver _resolver;
    private readonly EpisodeQuery _episodes;

    /// <summary>
    /// Initializes a new instance of the <see cref="PickEpisodesTool"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    public PickEpisodesTool(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
        _resolver = new ItemResolver(libraryManager);
        _episodes = new EpisodeQuery(libraryManager);
    }

    /// <inheritdoc />
    public string Name => "pick_episodes";

    /// <inheritdoc />
    public string Description =>
        "Pick episodes from one or more series in a single call. Returns real ids you can pass "
        + "straight to create_playlist or add_to_playlist. "
        + "If the user named specific shows, pass their names in \"series\". "
        + "If they asked for something random WITHOUT naming shows — \"a random mix\", \"surprise "
        + "me\", \"some series I have\" — do NOT invent famous titles from memory and do NOT ask "
        + "them to name one: omit \"series\" and set \"series_count\" instead, and this picks that "
        + "many real series from THIS library for you. "
        + "It reports every series it could not find or draw from, so never assume one was "
        + "included unless it appears in the result.";

    /// <inheritdoc />
    public bool IsMutating => false;

    /// <inheritdoc />
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["series"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Names (or ids) of the series to draw from. Omit this and set "
                                  + "series_count instead when nobody named specific shows.",
                ["items"] = new JsonObject { ["type"] = "string" }
            },
            ["series_count"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Instead of \"series\": draw this many series at random from "
                                  + "the user's own library. Use this for an unnamed random request.",
                ["minimum"] = 1,
                ["maximum"] = MaxSeries
            },
            ["count_per_series"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "How many episodes to take from each series (1-20).",
                ["minimum"] = 1,
                ["maximum"] = MaxPerSeries
            },
            ["random"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Draw at random. Default true; set false for the first episodes in order."
            },
            ["unwatched_only"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Only episodes this user has not finished."
            },
            ["season"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Optional season number, applied to every series."
            }
        },
        ["required"] = new JsonArray(),
        ["additionalProperties"] = false
    };

    /// <inheritdoc />
    public Task<JsonNode> ExecuteAsync(UserScope scope, JsonObject arguments, CancellationToken cancellationToken)
    {
        // Not a declared parameter, but observed live: asked to round out a random
        // list with a film and a book, the model passed kinds=["Movie","Book"] to this
        // tool anyway. It has no such parameter, the value was silently ignored, the
        // call failed for an unrelated reason (neither series nor series_count), and
        // rather than surface that, the model quietly drew more TV episodes instead —
        // so the person got a playlist that not only missed the film and book but gave
        // no sign anything had been dropped. Reading the stray argument here, even
        // though nothing declares it, turns that into a specific redirect instead of a
        // generic error the model is free to route around.
        var strayKinds = ToolArguments.GetStringList(arguments, "kinds");
        if (strayKinds.Any(k => !string.Equals(k, "Series", StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(k, "Episode", StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult<JsonNode>(new JsonObject
            {
                ["error"] = "pick_episodes only returns TV episodes — it has no \"kinds\" argument "
                            + "and cannot pick films, books or anything else.",
                ["what_to_do"] = "For films, books, or a mix of kinds, call search_library instead "
                                 + "with sort=\"random\" and kinds set to what you need. Do not "
                                 + "substitute more episodes for what was actually asked for."
            });
        }

        var names = ToolArguments.GetStringList(arguments, "series");
        var seriesCount = ToolArguments.GetInt(arguments, "series_count", 0);

        if (names.Count > 0)
        {
            var resolved = new List<Series>();
            var missingNames = new List<string>();

            foreach (var name in names.Take(MaxSeries))
            {
                var series = _resolver.ResolveSeries(scope, name);
                if (series is null)
                {
                    missingNames.Add(name);
                    continue;
                }

                resolved.Add(series);
            }

            if (resolved.Count == 0)
            {
                return Task.FromResult<JsonNode>(new JsonObject
                {
                    ["error"] = "None of those series names matched anything in this user's library.",
                    ["not_found"] = new JsonArray(missingNames.Select(n => (JsonNode)UntrustedContent.Sanitize(n)).ToArray()),
                    ["hint"] = "Call search_library with kinds=[\"Series\"] and no query to see the "
                               + "real names, or set series_count instead of series for a random pick."
                });
            }

            return Task.FromResult(Build(scope, arguments, resolved, missingNames));
        }

        if (seriesCount <= 0)
        {
            return Task.FromResult<JsonNode>(new JsonObject
            {
                ["error"] = "Pass either \"series\" with names, or \"series_count\" to draw random "
                            + "series from this library. Neither was given."
            });
        }

        var randomSeries = RandomSeries(scope, Math.Clamp(seriesCount, 1, MaxSeries));

        if (randomSeries.Count == 0)
        {
            return Task.FromResult<JsonNode>(new JsonObject
            {
                ["error"] = "This user's library has no series to draw from."
            });
        }

        return Task.FromResult(Build(scope, arguments, randomSeries, new List<string>()));
    }

    private IReadOnlyList<Series> RandomSeries(UserScope scope, int count)
    {
        var result = _libraryManager.GetItemsResult(new InternalItemsQuery(scope.User)
        {
            IncludeItemTypes = new[] { BaseItemKind.Series },
            Recursive = true,
            Limit = count,
            OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) }
        });

        return result.Items.OfType<Series>().ToList();
    }

    private JsonNode Build(
        UserScope scope,
        JsonObject arguments,
        IReadOnlyList<Series> targets,
        List<string> missingNames)
    {
        var perSeries = Math.Clamp(ToolArguments.GetInt(arguments, "count_per_series", 3), 1, MaxPerSeries);
        var random = ToolArguments.GetBool(arguments, "random", true);
        var unwatchedOnly = ToolArguments.GetBool(arguments, "unwatched_only", false);
        var season = ToolArguments.GetInt(arguments, "season", 0);

        var results = new JsonArray();
        var allIds = new JsonArray();
        var missing = new JsonArray(missingNames.Select(n => (JsonNode)UntrustedContent.Sanitize(n)).ToArray());

        foreach (var series in targets)
        {
            var (episodes, total) = _episodes.Read(scope, series, season, perSeries, random, unwatchedOnly);

            if (episodes.Count == 0)
            {
                missing.Add(UntrustedContent.Sanitize(series.Name));
                continue;
            }

            foreach (var episode in episodes)
            {
                allIds.Add(episode.Id.ToString("N"));
            }

            results.Add(new JsonObject
            {
                ["series"] = UntrustedContent.Sanitize(series.Name),
                ["series_id"] = series.Id.ToString("N"),
                ["available"] = total,
                ["episodes"] = EpisodeQuery.Project(episodes)
            });
        }

        var payload = new JsonObject
        {
            ["picked"] = results,
            ["all_item_ids"] = allIds,
            ["note"] = "all_item_ids is every id above in one list, ready for create_playlist "
                       + "or add_to_playlist. Pass those ids exactly.",

            // Looking the episodes up and then announcing them as though they had been
            // filed away is a failure this tool sees more than any other, because it is
            // the tool that runs just before the write. Saying so here, at the moment
            // the model is about to decide, works better than a rule in the prompt.
            ["if_the_user_asked_for_a_playlist"] =
                "You have NOT put these anywhere yet. Call create_playlist or add_to_playlist "
                + "now. Repeating these ids in your reply changes nothing on the server."
        };

        if (missing.Count > 0)
        {
            payload["not_found"] = missing;

            // The silent drop is the whole reason this tool exists, so it is stated as
            // an obligation rather than a note.
            payload["you_must_tell_the_user"] =
                "These series produced no episodes and are NOT in the ids above. Say so "
                + "explicitly instead of presenting the result as complete.";
        }

        return payload;
    }
}

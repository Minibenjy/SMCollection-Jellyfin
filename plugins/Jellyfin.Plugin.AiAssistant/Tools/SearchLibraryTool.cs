using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// Searches and browses the library the acting user is allowed to see.
/// </summary>
public sealed class SearchLibraryTool : IAssistantTool
{
    private const int MaxLimit = 25;

    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchLibraryTool"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    public SearchLibraryTool(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public string Name => "search_library";

    /// <inheritdoc />
    public string Description =>
        "Search or browse this user's media library. Returns only items the user is allowed to see. "
        + "Use this before answering any question about what the library contains.\n"
        + "To find something specific, pass the shortest distinctive fragment of its title, exactly "
        + "as it would be written: \"Super Mario\", not \"Super Mario movie\". Never put words like "
        + "movie, film, series, episode or season in the query — narrow the media type with kinds instead.\n"
        + "To list what the library holds rather than find one title, omit query entirely and pass "
        + "kinds, for example kinds=[\"Series\"] to list series. Omitting both lists everything.\n"
        + "For anything random or \"surprise me\", omit query and set sort=\"random\".\n"
        + "This does NOT find episodes of a named series — episode titles rarely contain the series "
        + "name. Use list_episodes or pick_episodes for that.\n"
        + "If a search returns nothing, retry once with fewer words before concluding the library "
        + "does not have it.\n"
        + "For a RECOMMENDATION request — a plot, mood, decade or premise rather than a title — do not "
        + "invent a title from memory and search for that. Instead narrow with genres, year_from/"
        + "year_to and kinds, leave query empty, and read the overview of each result returned: the "
        + "match has to come from what this library's own metadata says, never from what you recall "
        + "about a film with a similar-sounding plot. If nothing returned actually matches what was "
        + "described, say that plainly rather than recommending the closest genre match anyway.\n"
        + "To find what features a specific actor, director or writer, set person to their name.";

    /// <inheritdoc />
    public bool IsMutating => false;

    /// <inheritdoc />
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["query"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Title fragment to search for. Omit to browse instead of search."
            },
            ["kinds"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Optional media kinds to restrict the search to.",
                ["items"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(ItemProjection.SelectableKinds.Select(k => (JsonNode)k).ToArray())
                }
            },
            ["genres"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Optional genres to require, written as the library spells them.",
                ["items"] = new JsonObject { ["type"] = "string" }
            },
            ["person"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Only items featuring this actor, director or writer, by name."
            },
            ["year_from"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Only items from this production year onward. Use with year_to for a decade."
            },
            ["year_to"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Only items up to this production year."
            },
            ["sort"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Ordering when browsing. Use \"random\" for a random selection.",
                ["enum"] = new JsonArray("newest", "oldest", "name", "random", "rating", "runtime")
            },
            ["watched"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Restrict to items this user has or has not finished.",
                ["enum"] = new JsonArray("any", "watched", "unwatched")
            },
            ["favorites_only"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Only items the user marked as a favourite."
            },
            ["limit"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Maximum results to return (1-25).",
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
        // An empty query is a browse, not an error: "what series do I have" has no
        // search term, and without this the model is pushed into inventing one — which
        // is exactly how the literal word "series" ended up being searched for.
        var query = (ToolArguments.GetString(arguments, "query") ?? string.Empty).Trim();
        var limit = Math.Clamp(ToolArguments.GetInt(arguments, "limit", 10), 1, MaxLimit);

        if (!ItemProjection.TryParseKinds(ToolArguments.GetStringList(arguments, "kinds"), out var kinds, out var kindError))
        {
            return Task.FromResult<JsonNode>(new JsonObject { ["error"] = kindError });
        }

        var filters = ReadFilters(arguments);

        // person/year are frequently over-eager guesses on a small model's part — one
        // request for an animated film about a musical monkey came with
        // person="Actor Mono", a name nobody has, which zeroed out an otherwise correct
        // genre match. Genres are the filter a request is actually built around; person
        // and years are refinements, so if the full filter set finds nothing, they are
        // dropped before the genre-only search is given up on.
        foreach (var candidateFilters in FilterAttempts(filters))
        {
            foreach (var attempt in BuildAttempts(query))
            {
                var result = Run(scope, attempt, kinds, candidateFilters, limit);
                if (result.TotalRecordCount == 0)
                {
                    continue;
                }

                var payload = new JsonObject
                {
                    ["total"] = result.TotalRecordCount,
                    ["items"] = ItemProjection.ProjectAll(result.Items)
                };

                // Searching episodes by a remembered title is the failure this guards. The
                // model finds some episode of the right series and asserts it is the one the
                // user named — observed live: a file called "Kim Possible S02E31" was
                // reported as "Gone Fishin'" purely from training data. A hit here is not
                // evidence of a title match, and the result has to say so.
                if (kinds.Contains(BaseItemKind.Episode) && !string.IsNullOrWhiteSpace(attempt))
                {
                    payload["episode_warning"] =
                        "These matched on the EPISODE's own name, not on the series it belongs to, "
                        + "so this is never a reliable way to get episodes of a given series — it "
                        + "silently returns only the few whose own titles happen to contain your "
                        + "words. For episodes of a series, always call list_episodes or "
                        + "pick_episodes with the series name instead. Also do NOT claim any of "
                        + "these is an episode the user named unless the name here actually "
                        + "matches: library names may be filenames or translations.";
                }

                // Tell the model what actually matched. Without this it would report the
                // broadened query's results as answers to the narrower one it asked for.
                if (!string.Equals(attempt, query, StringComparison.Ordinal))
                {
                    payload["searched_for"] = attempt;
                    payload["note"] = "The original query found nothing, so it was broadened to "
                                      + "the text in searched_for. Say what was actually matched.";
                }

                if (!ReferenceEquals(candidateFilters, filters))
                {
                    payload["filters_relaxed"] =
                        "The person/year filter matched nothing, so these results are from the "
                        + "genre and kind filters alone. Do not claim they match the person or "
                        + "year that was asked for unless you can see that from the item itself.";
                }

                return Task.FromResult<JsonNode>(payload);
            }
        }

        var empty = new JsonObject
        {
            ["total"] = 0,
            ["items"] = new JsonArray()
        };

        empty["hint"] = string.IsNullOrWhiteSpace(query)
            ? "Nothing of that kind is in this user's library, with the filters you passed."
            : kinds.Contains(BaseItemKind.Episode)
            ? "No episode's own title matched. This searches episode titles, not the series "
              + "they belong to — if you wanted episodes of a series, call list_episodes or "
              + "pick_episodes with the series name instead."
            : "No match, including after automatically retrying with a shorter query. "
              + "The library metadata may be in a different language than the title you "
              + "used — try the title as it would be written locally. Otherwise the library "
              + "does not have it.";

        return Task.FromResult<JsonNode>(empty);
    }

    /// <summary>
    /// Builds the sequence of queries to try, widest last.
    /// </summary>
    /// <remarks>
    /// Models over-specify: they search "Super Mario movie" or compose a title they
    /// half-remember, like "Call of the Bible Black". Both return nothing against a
    /// library holding the real title. Returning an empty result with advice to retry
    /// does not work — observed against a live model, the advice was simply ignored and
    /// the user was told the film was absent. So the retry happens here, where it is a
    /// mechanism rather than a suggestion.
    /// </remarks>
    private static IEnumerable<string> BuildAttempts(string query)
    {
        yield return query;

        if (string.IsNullOrWhiteSpace(query))
        {
            yield break;
        }

        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Drop media-type nouns the model tacks on; they are never part of a title.
        var meaningful = words.Where(w => !MediaWords.Contains(w)).ToArray();
        if (meaningful.Length > 0 && meaningful.Length != words.Length)
        {
            yield return string.Join(' ', meaningful);
        }

        // Then keep only the leading words, which is where a distinctive title lives.
        if (meaningful.Length > 2)
        {
            yield return string.Join(' ', meaningful.Take(2));
        }
    }

    private static readonly HashSet<string> MediaWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "movie", "movies", "film", "films", "series", "show", "shows", "episode", "episodes",
        "season", "seasons", "book", "books", "pelicula", "película", "peliculas", "películas",
        "serie", "capitulo", "capítulo", "temporada", "temporadas", "libro", "libros"
    };

    private static IEnumerable<LibraryFilters> FilterAttempts(LibraryFilters filters)
    {
        yield return filters;

        if (filters.Person is not null || filters.Years is not null)
        {
            yield return filters with { Person = null, Years = null };
        }
    }

    private static LibraryFilters ReadFilters(JsonObject arguments)
    {
        var watched = (ToolArguments.GetString(arguments, "watched") ?? "any").Trim();
        var person = (ToolArguments.GetString(arguments, "person") ?? string.Empty).Trim();
        var yearFrom = ToolArguments.GetInt(arguments, "year_from", 0);
        var yearTo = ToolArguments.GetInt(arguments, "year_to", 0);

        return new LibraryFilters(
            Sort: (ToolArguments.GetString(arguments, "sort") ?? string.Empty).Trim(),
            Genres: ToolArguments.GetStringList(arguments, "genres"),
            Played: watched.Equals("watched", StringComparison.OrdinalIgnoreCase) ? true
                : watched.Equals("unwatched", StringComparison.OrdinalIgnoreCase) ? false
                : null,
            FavoritesOnly: ToolArguments.GetBool(arguments, "favorites_only", false),
            Person: person.Length > 0 ? person : null,
            Years: YearRange(yearFrom, yearTo));
    }

    /// <summary>
    /// Turns year_from/year_to into the explicit year list Jellyfin's query wants.
    /// </summary>
    /// <remarks>
    /// Either end alone is enough to mean something ("from 1990 onward", "up to
    /// 1999"), and the two together are how a decade gets asked for. A span is capped
    /// so a model that passes year_from=1900 by mistake does not build a
    /// hundred-and-some-element array for a query that is really "no filter".
    /// </remarks>
    private static int[]? YearRange(int from, int to)
    {
        if (from <= 0 && to <= 0)
        {
            return null;
        }

        var start = from > 0 ? from : to;
        var end = to > 0 ? to : from;

        if (end < start)
        {
            (start, end) = (end, start);
        }

        end = Math.Min(end, start + 99);

        return Enumerable.Range(start, end - start + 1).ToArray();
    }

    private QueryResult<BaseItem> Run(
        UserScope scope,
        string query,
        BaseItemKind[] kinds,
        LibraryFilters filters,
        int limit)
    {
        // Constructing the query from the user applies Jellyfin's own access rules:
        // library permissions, parental rating ceiling and blocked tags. Authorization
        // is decided here, by the server, not by the model and not by this plugin.
        var internalQuery = new InternalItemsQuery(scope.User)
        {
            Recursive = true,
            Limit = limit,
            IncludeItemTypes = kinds
        };

        if (filters.Genres.Count > 0)
        {
            internalQuery.Genres = filters.Genres.ToArray();
        }

        if (filters.Played is not null)
        {
            internalQuery.IsPlayed = filters.Played;
        }

        if (filters.FavoritesOnly)
        {
            internalQuery.IsFavorite = true;
        }

        if (filters.Person is not null)
        {
            internalQuery.Person = filters.Person;
        }

        if (filters.Years is not null)
        {
            internalQuery.Years = filters.Years;
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            internalQuery.SearchTerm = query;
        }

        var order = SortOrderFor(filters.Sort, hasQuery: !string.IsNullOrWhiteSpace(query));
        if (order is not null)
        {
            internalQuery.OrderBy = order;
        }

        return _libraryManager.GetItemsResult(internalQuery);
    }

    /// <summary>
    /// Maps the model's <c>sort</c> word onto a Jellyfin ordering.
    /// </summary>
    /// <remarks>
    /// "random" is the one that matters. Without it a request for something random
    /// leaves the model nothing to call, and it searches for the literal word
    /// "random" — observed twice in a row in one exchange — then presents the first
    /// few search hits as a random selection, which they are not.
    ///
    /// A search with no explicit sort keeps Jellyfin's relevance order; a browse with
    /// no explicit sort gets newest-first, because an arbitrary slice reads as a
    /// broken library.
    /// </remarks>
    private static (ItemSortBy, SortOrder)[]? SortOrderFor(string sort, bool hasQuery)
        => sort.ToLowerInvariant() switch
        {
            "random" => new[] { (ItemSortBy.Random, SortOrder.Ascending) },
            "name" => new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
            "oldest" => new[] { (ItemSortBy.DateCreated, SortOrder.Ascending) },
            "newest" => new[] { (ItemSortBy.DateCreated, SortOrder.Descending) },
            "rating" => new[] { (ItemSortBy.CommunityRating, SortOrder.Descending) },
            "runtime" => new[] { (ItemSortBy.Runtime, SortOrder.Descending) },
            _ => hasQuery ? null : new[] { (ItemSortBy.DateCreated, SortOrder.Descending) }
        };

    private sealed record LibraryFilters(
        string Sort,
        IReadOnlyList<string> Genres,
        bool? Played,
        bool FavoritesOnly,
        string? Person,
        int[]? Years);
}

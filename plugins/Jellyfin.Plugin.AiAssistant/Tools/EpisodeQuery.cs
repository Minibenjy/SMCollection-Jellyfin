using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AiAssistant.Guardrails;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// The one place episodes are pulled out of a series.
/// </summary>
/// <remarks>
/// Both episode tools go through here so that "in order" and "at random" mean the
/// same thing in each, and so that a series that resolves in one resolves in the
/// other.
/// </remarks>
internal sealed class EpisodeQuery
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeQuery"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    public EpisodeQuery(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Reads episodes of one series as the acting user.
    /// </summary>
    /// <param name="scope">The acting user.</param>
    /// <param name="series">The series.</param>
    /// <param name="season">Season to restrict to, or zero for all.</param>
    /// <param name="limit">Maximum episodes.</param>
    /// <param name="random">Whether to draw at random rather than in order.</param>
    /// <param name="unwatchedOnly">Whether to exclude episodes the user has finished.</param>
    /// <returns>The episodes and the total that matched.</returns>
    public (IReadOnlyList<Episode> Episodes, int Total) Read(
        UserScope scope,
        Series series,
        int season,
        int limit,
        bool random,
        bool unwatchedOnly)
    {
        var query = new InternalItemsQuery(scope.User)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive = true,
            ParentId = series.Id,
            Limit = limit,
            OrderBy = random
                ? new[] { (ItemSortBy.Random, SortOrder.Ascending) }
                : new[]
                {
                    (ItemSortBy.ParentIndexNumber, SortOrder.Ascending),
                    (ItemSortBy.IndexNumber, SortOrder.Ascending)
                }
        };

        if (season > 0)
        {
            query.ParentIndexNumber = season;
        }

        if (unwatchedOnly)
        {
            query.IsPlayed = false;
        }

        var result = _libraryManager.GetItemsResult(query);
        var episodes = result.Items.OfType<Episode>().ToList();

        // A random draw is still asked for in broadcast order by the caller when it
        // reports back, otherwise "3 random episodes" reads as a shuffled mess.
        if (random)
        {
            episodes = episodes
                .OrderBy(e => e.ParentIndexNumber ?? 0)
                .ThenBy(e => e.IndexNumber ?? 0)
                .ToList();
        }

        return (episodes, result.TotalRecordCount);
    }

    /// <summary>
    /// Projects episodes for a tool result.
    /// </summary>
    /// <param name="episodes">The episodes.</param>
    /// <returns>The array.</returns>
    public static JsonArray Project(IEnumerable<Episode> episodes)
    {
        var array = new JsonArray();
        foreach (var episode in episodes)
        {
            array.Add(new JsonObject
            {
                ["id"] = episode.Id.ToString("N"),
                ["season"] = episode.ParentIndexNumber,
                ["episode"] = episode.IndexNumber,
                ["name"] = UntrustedContent.Sanitize(episode.Name)
            });
        }

        return array;
    }
}

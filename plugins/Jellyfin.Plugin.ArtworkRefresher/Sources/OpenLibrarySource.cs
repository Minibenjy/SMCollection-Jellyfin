using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;
using Jellyfin.Plugin.ArtworkRefresher.Core;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ArtworkRefresher.Sources;

/// <summary>
/// Open Library covers for books. The primary image of a book is protected by default, so this
/// source is only reached for books when the administrator turns that protection off or forces
/// one item.
/// </summary>
public sealed class OpenLibrarySource : IArtworkSource
{
    private const string Search = "https://openlibrary.org/search.json";
    private const string Covers = "https://covers.openlibrary.org/b/id/";

    /// <inheritdoc />
    public string Id => SourceIds.OpenLibrary;

    /// <summary>
    /// Parses a search response into candidates.
    /// </summary>
    /// <param name="root">The response.</param>
    /// <param name="query">The query.</param>
    /// <returns>The candidates.</returns>
    public static List<ArtworkCandidate> Parse(JsonElement root, ArtworkQuery query)
    {
        var list = new List<ArtworkCandidate>();
        if (!root.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        var rank = 0;
        foreach (var d in docs.EnumerateArray())
        {
            var cover = SourceHelpers.Int(d, "cover_i");
            var title = SourceHelpers.Str(d, "title");
            if (cover is null or <= 0 || title is null || !SourceHelpers.NameMatches(query.Name, title))
            {
                continue;
            }

            var key = SourceHelpers.Str(d, "key");
            list.Add(new ArtworkCandidate
            {
                Source = SourceIds.OpenLibrary,
                RemoteId = cover.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Uri = new Uri(Covers + cover.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-L.jpg"),
                ImageType = ImageType.Primary,
                Score = 2 - (rank++ * 0.1),
                Attribution = new ArtworkAttribution(null, "Open Library", "https://openlibrary.org/developers/api", key is null ? null : "https://openlibrary.org" + key)
            });
        }

        return list;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtworkCandidate>> FindAsync(
        ArtworkQuery query,
        IArtworkHttp http,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (query.Kind != BaseItemKind.Book || !query.Wants(ImageType.Primary))
        {
            return [];
        }

        var uri = SourceHelpers.Url(Search, ("title", query.Name), ("author", query.CreatorName), ("limit", "5"), ("fields", "key,title,cover_i"));
        using var doc = await http.GetJsonAsync(SourceIds.OpenLibrary, uri, configuration.Sources.OpenLibrary.RequestsPerMinute, null, cancellationToken).ConfigureAwait(false);
        return doc is null ? [] : Parse(doc.RootElement, query);
    }
}

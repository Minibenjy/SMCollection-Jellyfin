using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;
using Jellyfin.Plugin.ArtworkRefresher.Core;

namespace Jellyfin.Plugin.ArtworkRefresher.Sources;

/// <summary>
/// Reads text metadata (overview, genres, year, rating, studios, tagline) from The Movie Database. It only
/// goes by ids the item already has (TMDb id, or IMDb id through the find endpoint): a name search could
/// put the wrong text on an item, so there is none.
/// </summary>
public static class TmdbMetadataSource
{
    private const string Api = "https://api.themoviedb.org/3/";

    /// <summary>Parses a movie, show, episode or person response.</summary>
    /// <param name="root">The response.</param>
    /// <param name="kind">The item kind.</param>
    /// <returns>The metadata.</returns>
    public static RemoteMetadata Parse(JsonElement root, BaseItemKind kind)
    {
        var meta = new RemoteMetadata();
        if (kind == BaseItemKind.Person)
        {
            meta.Overview = Clean(SourceHelpers.Str(root, "biography"));
            return meta;
        }

        meta.Overview = Clean(SourceHelpers.Str(root, "overview"));
        meta.Tagline = Clean(SourceHelpers.Str(root, "tagline"));
        var date = SourceHelpers.Str(root, kind switch { BaseItemKind.Series => "first_air_date", BaseItemKind.Episode => "air_date", _ => "release_date" });
        if (date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) && year > 1800)
        {
            meta.ProductionYear = year;
        }

        var rating = SourceHelpers.Num(root, "vote_average");
        var votes = SourceHelpers.Num(root, "vote_count") ?? 0;
        if (rating is > 0 && votes > 0)
        {
            meta.CommunityRating = (float)Math.Round(rating.Value, 1);
        }

        meta.Genres = Names(root, "genres");
        meta.Studios = kind == BaseItemKind.Series
            ? Names(root, "networks").Concat(Names(root, "production_companies")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : Names(root, "production_companies");
        return meta;
    }

    /// <summary>Fetches the metadata of an item.</summary>
    /// <param name="query">What is known about the item.</param>
    /// <param name="http">The guarded HTTP client.</param>
    /// <param name="configuration">The configuration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The metadata, or null when TMDb is off, the item has no usable id or the call failed.</returns>
    public static async Task<RemoteMetadata?> FetchAsync(ArtworkQuery query, IArtworkHttp http, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        var settings = configuration.Sources.Tmdb;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return null;
        }

        var key = settings.ApiKey.Trim();
        var bearer = key.StartsWith("eyJ", StringComparison.Ordinal);
        Dictionary<string, string>? headers = bearer ? new() { ["Authorization"] = "Bearer " + key } : null;
        var language = query.Languages.Count > 0 ? query.Languages[0] : null;

        async Task<JsonDocument?> Get(string path, params (string, string?)[] extra)
        {
            var all = new List<(string, string?)>(extra) { ("api_key", bearer ? null : key) };
            return await http.GetJsonAsync(SourceIds.Tmdb, SourceHelpers.Url(Api + path, all.ToArray()), settings.RequestsPerMinute, headers, cancellationToken).ConfigureAwait(false);
        }

        async Task<string?> FromImdb(string resultsKey)
        {
            var imdb = query.Id("Imdb");
            if (string.IsNullOrEmpty(imdb))
            {
                return null;
            }

            using var doc = await Get("find/" + Uri.EscapeDataString(imdb), ("external_source", "imdb_id")).ConfigureAwait(false);
            return doc is not null && doc.RootElement.TryGetProperty(resultsKey, out var arr) && arr.ValueKind == JsonValueKind.Array
                   && arr.GetArrayLength() > 0 && arr[0].TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                ? id.GetInt64().ToString(CultureInfo.InvariantCulture)
                : null;
        }

        string? path = null;
        switch (query.Kind)
        {
            case BaseItemKind.Movie:
            {
                var id = query.Id("Tmdb") ?? await FromImdb("movie_results").ConfigureAwait(false);
                path = id is null ? null : "movie/" + Uri.EscapeDataString(id);
                break;
            }

            case BaseItemKind.Series:
            {
                var id = query.Id("Tmdb") ?? await FromImdb("tv_results").ConfigureAwait(false);
                path = id is null ? null : "tv/" + Uri.EscapeDataString(id);
                break;
            }

            case BaseItemKind.Episode when query.SeriesId("Tmdb") is { } sid && query.SeasonNumber is { } sn && query.EpisodeNumber is { } en:
                path = "tv/" + Uri.EscapeDataString(sid) + "/season/" + sn.ToString(CultureInfo.InvariantCulture) + "/episode/" + en.ToString(CultureInfo.InvariantCulture);
                break;
            case BaseItemKind.Person when query.Id("Tmdb") is { } pid:
                path = "person/" + Uri.EscapeDataString(pid);
                break;
        }

        if (path is null)
        {
            return null;
        }

        using var response = await Get(path, ("language", language)).ConfigureAwait(false);
        return response is null ? null : Parse(response.RootElement, query.Kind);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string[] Names(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Select(e => SourceHelpers.Str(e, "name"))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

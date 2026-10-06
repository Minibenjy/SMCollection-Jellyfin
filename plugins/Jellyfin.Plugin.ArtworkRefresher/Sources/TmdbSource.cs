using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;
using Jellyfin.Plugin.ArtworkRefresher.Core;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ArtworkRefresher.Sources;

/// <summary>
/// The Movie Database. Uses the ids already on the item first; a text search runs only when the
/// item has no usable id. Requires the user's own API key.
/// </summary>
public sealed class TmdbSource : IArtworkSource
{
    private const string Api = "https://api.themoviedb.org/3/";
    private const string ImageBase = "https://image.tmdb.org/t/p/";

    /// <inheritdoc />
    public string Id => SourceIds.Tmdb;

    /// <summary>
    /// Parses an images response into candidates.
    /// </summary>
    /// <param name="root">The response.</param>
    /// <param name="query">The query.</param>
    /// <param name="sizes">The size tokens (poster, backdrop, logo).</param>
    /// <param name="postersFromProfiles">True for people (profiles fill the primary image).</param>
    /// <param name="logosAsPrimary">True for companies (the logo is the primary image).</param>
    /// <returns>The candidates.</returns>
    public static List<ArtworkCandidate> ParseImages(
        JsonElement root,
        ArtworkQuery query,
        (string Poster, string Backdrop, string Logo) sizes,
        bool postersFromProfiles,
        bool logosAsPrimary)
    {
        var list = new List<ArtworkCandidate>();
        void Collect(string property, ImageType type, string size, bool neutralPreferred)
        {
            if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var e in array.EnumerateArray())
            {
                var path = SourceHelpers.Str(e, "file_path");
                if (string.IsNullOrEmpty(path) || !path.StartsWith('/'))
                {
                    continue;
                }

                var lang = SourceHelpers.Str(e, "iso_639_1");
                var ls = SourceHelpers.LanguageScore(lang, query, neutralPreferred);
                if (ls is null)
                {
                    continue;
                }

                var w = SourceHelpers.Int(e, "width");
                var h = SourceHelpers.Int(e, "height");
                var votes = SourceHelpers.Num(e, "vote_average") ?? 0;
                list.Add(new ArtworkCandidate
                {
                    Source = SourceIds.Tmdb,
                    RemoteId = path,
                    Uri = new Uri(ImageBase + size + path),
                    ImageType = type,
                    Width = w,
                    Height = h,
                    Language = string.IsNullOrWhiteSpace(lang) ? null : lang,
                    Score = ls.Value + (votes / 10.0) + SourceHelpers.ResolutionBonus(w, h),
                    Attribution = new ArtworkAttribution(null, "TMDB", "https://www.themoviedb.org/terms-of-use", null)
                });
            }
        }

        if (logosAsPrimary)
        {
            Collect("logos", ImageType.Primary, sizes.Logo, false);
            Collect("logos", ImageType.Logo, sizes.Logo, false);
        }
        else
        {
            Collect(postersFromProfiles ? "profiles" : "posters", ImageType.Primary, sizes.Poster, false);
            Collect("stills", ImageType.Primary, sizes.Backdrop, false);
            Collect("backdrops", ImageType.Backdrop, sizes.Backdrop, true);
            Collect("logos", ImageType.Logo, sizes.Logo, false);
            Collect("backdrops", ImageType.Thumb, sizes.Backdrop, true);
        }

        return list.OrderByDescending(c => c.Score).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtworkCandidate>> FindAsync(
        ArtworkQuery query,
        IArtworkHttp http,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var settings = configuration.Sources.Tmdb;
        var sizes = (configuration.Sources.TmdbPosterSize, configuration.Sources.TmdbBackdropSize, configuration.Sources.TmdbLogoSize);
        var headers = AuthHeaders(settings.ApiKey);
        string? keyParam = headers is null ? settings.ApiKey.Trim() : null;
        var langs = string.Join(',', query.Languages.Concat(query.IncludeNeutral ? ["null"] : []));
        var langParam = query.Languages.Count > 0 || query.IncludeNeutral ? langs : null;

        async Task<JsonDocument?> Get(string path, params (string, string?)[] extra)
        {
            var all = new List<(string, string?)>(extra) { ("api_key", keyParam) };
            return await http.GetJsonAsync(SourceIds.Tmdb, SourceHelpers.Url(Api + path, all.ToArray()), settings.RequestsPerMinute, headers, cancellationToken).ConfigureAwait(false);
        }

        async Task<string?> FirstId(string path, string name, string? year = null)
        {
            using var doc = await Get(path, ("query", name), ("year", year)).ConfigureAwait(false);
            if (doc is null || !doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            // Only an exact (case-insensitive) name match: a loose first hit would put the wrong picture on an item.
            foreach (var r in results.EnumerateArray())
            {
                var title = SourceHelpers.Str(r, "title") ?? SourceHelpers.Str(r, "name");
                if (title is not null && string.Equals(title.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase)
                    && r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                {
                    return id.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            return null;
        }

        string? imagesPath = null;
        bool profiles = false;
        bool logosPrimary = false;

        switch (query.Kind)
        {
            case BaseItemKind.Movie:
            {
                var id = query.Id("Tmdb") ?? await FindByImdb(query.Id("Imdb"), "movie_results").ConfigureAwait(false)
                         ?? await FirstId("search/movie", query.Name, query.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
                imagesPath = id is null ? null : "movie/" + id + "/images";
                break;
            }

            case BaseItemKind.Series:
            {
                var id = query.Id("Tmdb") ?? await FindByImdb(query.Id("Imdb"), "tv_results").ConfigureAwait(false)
                         ?? await FirstId("search/tv", query.Name).ConfigureAwait(false);
                imagesPath = id is null ? null : "tv/" + id + "/images";
                break;
            }

            case BaseItemKind.Season when query.SeriesId("Tmdb") is { } sid && query.SeasonNumber is { } sn:
                imagesPath = "tv/" + sid + "/season/" + sn + "/images";
                break;
            case BaseItemKind.Episode when query.SeriesId("Tmdb") is { } sid && query.SeasonNumber is { } sn && query.EpisodeNumber is { } en:
                imagesPath = "tv/" + sid + "/season/" + sn + "/episode/" + en + "/images";
                break;
            case BaseItemKind.Person:
            {
                var id = query.Id("Tmdb") ?? await FirstId("search/person", query.Name).ConfigureAwait(false);
                imagesPath = id is null ? null : "person/" + id + "/images";
                profiles = true;
                break;
            }

            case BaseItemKind.BoxSet:
            {
                var id = query.Id("Tmdb") ?? await FirstId("search/collection", query.Name).ConfigureAwait(false);
                imagesPath = id is null ? null : "collection/" + id + "/images";
                break;
            }

            case BaseItemKind.Studio:
            {
                var id = await FirstId("search/company", query.Name).ConfigureAwait(false);
                imagesPath = id is null ? null : "company/" + id + "/images";
                logosPrimary = true;
                break;
            }
        }

        async Task<string?> FindByImdb(string? imdb, string resultsKey)
        {
            if (string.IsNullOrEmpty(imdb))
            {
                return null;
            }

            using var doc = await Get("find/" + Uri.EscapeDataString(imdb), ("external_source", "imdb_id")).ConfigureAwait(false);
            if (doc is not null && doc.RootElement.TryGetProperty(resultsKey, out var arr) && arr.ValueKind == JsonValueKind.Array
                && arr.GetArrayLength() > 0 && arr[0].TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
            {
                return id.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return null;
        }

        if (imagesPath is null)
        {
            return [];
        }

        using var images = await Get(imagesPath, ("include_image_language", langParam)).ConfigureAwait(false);
        return images is null ? [] : ParseImages(images.RootElement, query, sizes, profiles, logosPrimary);
    }

    private static Dictionary<string, string>? AuthHeaders(string key)
        => key.Trim().StartsWith("eyJ", StringComparison.Ordinal)
            ? new Dictionary<string, string> { ["Authorization"] = "Bearer " + key.Trim() }
            : null;
}

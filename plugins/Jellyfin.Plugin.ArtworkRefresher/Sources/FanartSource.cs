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
/// Fanart.tv. Works from ids only (TMDb for movies, TheTVDB for shows, MusicBrainz for music):
/// it never searches by name. Needs the user's own key.
/// </summary>
public sealed class FanartSource : IArtworkSource
{
    private const string Api = "https://webservice.fanart.tv/v3/";

    /// <inheritdoc />
    public string Id => SourceIds.Fanart;

    /// <summary>
    /// Parses a Fanart.tv response into candidates.
    /// </summary>
    /// <param name="root">The response.</param>
    /// <param name="query">The query.</param>
    /// <param name="map">Fanart property name to image type.</param>
    /// <param name="seasonFilter">When set, only entries of that season (season posters).</param>
    /// <returns>The candidates.</returns>
    public static List<ArtworkCandidate> Parse(JsonElement root, ArtworkQuery query, IReadOnlyList<(string Property, ImageType Type)> map, int? seasonFilter = null)
    {
        var list = new List<ArtworkCandidate>();
        foreach (var (property, type) in map)
        {
            if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var e in array.EnumerateArray())
            {
                var url = SourceHelpers.Str(e, "url");
                if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    continue;
                }

                if (seasonFilter is { } sf && (SourceHelpers.Int(e, "season") ?? -1) != sf)
                {
                    continue;
                }

                var lang = SourceHelpers.Str(e, "lang");
                var ls = SourceHelpers.LanguageScore(lang, query, type is ImageType.Backdrop or ImageType.Thumb);
                if (ls is null)
                {
                    continue;
                }

                var likes = SourceHelpers.Int(e, "likes") ?? 0;
                list.Add(new ArtworkCandidate
                {
                    Source = SourceIds.Fanart,
                    RemoteId = SourceHelpers.Str(e, "id"),
                    Uri = uri,
                    ImageType = type,
                    Language = string.IsNullOrWhiteSpace(lang) ? null : lang,
                    Score = ls.Value + Math.Min(2.0, likes / 20.0),
                    Attribution = new ArtworkAttribution(null, "Fanart.tv", "https://fanart.tv/", null)
                });
            }
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
        var settings = configuration.Sources.Fanart;
        string? path = null;
        IReadOnlyList<(string, ImageType)> map = [];
        int? season = null;
        bool albums = false;
        string? albumId = null;

        switch (query.Kind)
        {
            case BaseItemKind.Movie when (query.Id("Tmdb") ?? query.Id("Imdb")) is { } mid:
                path = "movies/" + mid;
                map = [("movieposter", ImageType.Primary), ("moviebackground", ImageType.Backdrop), ("hdmovielogo", ImageType.Logo), ("moviethumb", ImageType.Thumb)];
                break;
            case BaseItemKind.Series when query.Id("Tvdb") is { } tid:
                path = "tv/" + tid;
                map = [("tvposter", ImageType.Primary), ("showbackground", ImageType.Backdrop), ("hdtvlogo", ImageType.Logo), ("tvthumb", ImageType.Thumb)];
                break;
            case BaseItemKind.Season when query.SeriesId("Tvdb") is { } tid && query.SeasonNumber is { } sn:
                path = "tv/" + tid;
                season = sn;
                map = [("seasonposter", ImageType.Primary), ("seasonthumb", ImageType.Thumb)];
                break;
            case BaseItemKind.MusicArtist when query.Id("MusicBrainzArtist") is { } aid:
                path = "music/" + aid;
                map = [("artistthumb", ImageType.Primary), ("artistbackground", ImageType.Backdrop), ("hdmusiclogo", ImageType.Logo)];
                break;
            case BaseItemKind.MusicAlbum when query.Id("MusicBrainzReleaseGroup") is { } rg:
                path = "music/albums/" + rg;
                albums = true;
                albumId = rg;
                map = [("albumcover", ImageType.Primary)];
                break;
        }

        if (path is null)
        {
            return [];
        }

        var uri = SourceHelpers.Url(Api + path, ("api_key", settings.ApiKey.Trim()), ("client_key", settings.ApiKey2.Trim()));
        using var doc = await http.GetJsonAsync(SourceIds.Fanart, uri, settings.RequestsPerMinute, null, cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            return [];
        }

        var root = doc.RootElement;
        if (albums && albumId is not null)
        {
            // Album covers sit under albums.<release group id>.albumcover.
            if (!root.TryGetProperty("albums", out var albumsNode) || !albumsNode.TryGetProperty(albumId, out var one))
            {
                return [];
            }

            root = one;
        }

        return Parse(root, query, map.ToList(), season);
    }
}

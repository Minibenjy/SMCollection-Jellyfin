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
/// Google Programmable Search (image search) through the official API with the user's own key
/// and engine id. Off by default, always the last resort, SafeSearch on by default. There is no
/// HTML scraping anywhere in this plugin. A rights filter does not make an image free to reuse.
/// </summary>
public sealed class GoogleCseSource : IArtworkSource
{
    private const string Api = "https://www.googleapis.com/customsearch/v1";

    /// <inheritdoc />
    public string Id => SourceIds.GoogleCse;

    /// <summary>
    /// Builds the search text for an item and image type.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <param name="type">The image type.</param>
    /// <returns>The text.</returns>
    public static string BuildText(ArtworkQuery query, ImageType type)
    {
        var parts = new List<string> { query.Name };
        if (query.Year is { } y)
        {
            parts.Add(y.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        parts.Add(type == ImageType.Primary
            ? query.Kind switch
            {
                BaseItemKind.Movie or BaseItemKind.Series or BaseItemKind.Season or BaseItemKind.BoxSet => "poster",
                BaseItemKind.MusicAlbum => "album cover",
                BaseItemKind.Book => "book cover",
                _ => string.Empty
            }
            : type == ImageType.Logo ? "logo" : "wallpaper");
        return string.Join(' ', parts.Where(p => p.Length > 0));
    }

    /// <summary>
    /// Parses a search response into candidates.
    /// </summary>
    /// <param name="root">The response.</param>
    /// <param name="type">The image type searched.</param>
    /// <returns>The candidates.</returns>
    public static List<ArtworkCandidate> Parse(JsonElement root, ImageType type)
    {
        var list = new List<ArtworkCandidate>();
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        var rank = 0;
        foreach (var item in items.EnumerateArray())
        {
            var link = SourceHelpers.Str(item, "link");
            if (!SourceHelpers.TryHttps(link, out var uri))
            {
                continue;
            }

            int? w = null, h = null;
            string? page = null;
            if (item.TryGetProperty("image", out var image))
            {
                w = SourceHelpers.Int(image, "width");
                h = SourceHelpers.Int(image, "height");
                page = SourceHelpers.Str(image, "contextLink");
            }

            list.Add(new ArtworkCandidate
            {
                Source = SourceIds.GoogleCse,
                RemoteId = link,
                Uri = uri,
                ImageType = type,
                Width = w,
                Height = h,
                Score = 1 - (rank++ * 0.05),
                AnyPublicHost = true,
                Attribution = new ArtworkAttribution(null, "Unknown (web image search result)", null, page)
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
        var s = configuration.Sources;
        var results = new List<ArtworkCandidate>();
        foreach (var type in query.WantedTypes.Where(t => t is ImageType.Primary or ImageType.Backdrop or ImageType.Logo).Distinct())
        {
            var uri = SourceHelpers.Url(
                Api,
                ("key", s.GoogleCse.ApiKey.Trim()),
                ("cx", s.GoogleCse.ApiKey2.Trim()),
                ("q", BuildText(query, type)),
                ("searchType", "image"),
                ("num", "5"),
                ("safe", s.GoogleSafeSearch ? "active" : "off"),
                ("imgSize", type == ImageType.Primary ? "large" : "xlarge"),
                ("rights", s.GoogleRightsFilter));
            using var doc = await http.GetJsonAsync(SourceIds.GoogleCse, uri, s.GoogleCse.RequestsPerMinute, null, cancellationToken).ConfigureAwait(false);
            if (doc is not null)
            {
                results.AddRange(Parse(doc.RootElement, type));
            }
        }

        return results;
    }
}

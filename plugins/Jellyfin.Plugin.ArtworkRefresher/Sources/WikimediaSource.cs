using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;
using Jellyfin.Plugin.ArtworkRefresher.Core;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ArtworkRefresher.Sources;

/// <summary>
/// Wikimedia Commons file search. Every file has its own licence, so a candidate is only
/// produced when the licence is known and not a non-free one, and the licence and author travel
/// with the candidate. Used for people, studios, artists and, when the file name says "poster",
/// titles. The file name must contain every word of the name searched.
/// </summary>
public sealed partial class WikimediaSource : IArtworkSource
{
    private const string Api = "https://commons.wikimedia.org/w/api.php";

    /// <inheritdoc />
    public string Id => SourceIds.Wikimedia;

    /// <summary>
    /// Parses a Commons search response into candidates.
    /// </summary>
    /// <param name="root">The response.</param>
    /// <param name="query">The query.</param>
    /// <param name="requirePoster">Whether the file name must contain "poster".</param>
    /// <returns>The candidates.</returns>
    public static List<ArtworkCandidate> Parse(JsonElement root, ArtworkQuery query, bool requirePoster)
    {
        var list = new List<ArtworkCandidate>();
        if (!root.TryGetProperty("query", out var q) || !q.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Object)
        {
            return list;
        }

        foreach (var page in pages.EnumerateObject())
        {
            var title = SourceHelpers.Str(page.Value, "title") ?? string.Empty;
            if (!SourceHelpers.NameMatches(query.Name, title) || (requirePoster && !title.Contains("poster", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!page.Value.TryGetProperty("imageinfo", out var infos) || infos.ValueKind != JsonValueKind.Array || infos.GetArrayLength() == 0)
            {
                continue;
            }

            var info = infos[0];
            var mime = SourceHelpers.Str(info, "mime");
            if (mime is not ("image/jpeg" or "image/png" or "image/webp" or "image/svg+xml"))
            {
                continue;
            }

            var meta = info.TryGetProperty("extmetadata", out var m) ? m : default;
            var license = MetaValue(meta, "LicenseShortName");
            if (string.IsNullOrWhiteSpace(license) || IsNonFree(license))
            {
                continue;
            }

            // The thumbnail URL is a raster (a PNG even for an SVG) at a sensible size.
            var url = SourceHelpers.Str(info, "thumburl") ?? SourceHelpers.Str(info, "url");
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var w = SourceHelpers.Int(info, "thumbwidth") ?? SourceHelpers.Int(info, "width");
            var h = SourceHelpers.Int(info, "thumbheight") ?? SourceHelpers.Int(info, "height");
            list.Add(new ArtworkCandidate
            {
                Source = SourceIds.Wikimedia,
                RemoteId = title,
                Uri = uri,
                ImageType = ImageType.Primary,
                Width = w,
                Height = h,
                Score = 1 + SourceHelpers.ResolutionBonus(w, h),
                Attribution = new ArtworkAttribution(
                    StripHtml(MetaValue(meta, "Artist")),
                    license,
                    MetaValue(meta, "LicenseUrl"),
                    SourceHelpers.Str(info, "descriptionurl"))
            });
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
        if (!query.Wants(ImageType.Primary))
        {
            return [];
        }

        var requirePoster = query.Kind is BaseItemKind.Movie or BaseItemKind.Series;
        if (query.Kind is not (BaseItemKind.Person or BaseItemKind.Studio or BaseItemKind.MusicArtist or BaseItemKind.Movie or BaseItemKind.Series))
        {
            return [];
        }

        var term = "filetype:bitmap " + query.Name + (requirePoster ? " poster" : string.Empty);
        var uri = SourceHelpers.Url(
            Api,
            ("action", "query"),
            ("format", "json"),
            ("generator", "search"),
            ("gsrnamespace", "6"),
            ("gsrlimit", "8"),
            ("gsrsearch", term),
            ("prop", "imageinfo"),
            ("iiprop", "url|size|mime|extmetadata"),
            ("iiurlwidth", "1200"),
            ("iiextmetadatafilter", "LicenseShortName|LicenseUrl|Artist"));
        using var doc = await http.GetJsonAsync(SourceIds.Wikimedia, uri, configuration.Sources.Wikimedia.RequestsPerMinute, null, cancellationToken).ConfigureAwait(false);
        return doc is null ? [] : Parse(doc.RootElement, query, requirePoster);
    }

    private static string? MetaValue(JsonElement meta, string name)
        => meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(name, out var v) ? SourceHelpers.Str(v, "value") : null;

    private static bool IsNonFree(string license)
        => license.Contains("fair use", StringComparison.OrdinalIgnoreCase)
           || license.Contains("non-free", StringComparison.OrdinalIgnoreCase)
           || license.Contains("copyrighted", StringComparison.OrdinalIgnoreCase)
           || license.Contains("all rights reserved", StringComparison.OrdinalIgnoreCase);

    private static string? StripHtml(string? html)
        => html is null ? null : HtmlTag().Replace(html, string.Empty).Trim();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex HtmlTag();
}

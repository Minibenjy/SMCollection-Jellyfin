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
/// Cover Art Archive: the front cover of an album, found through its MusicBrainz release
/// (or release group) id. Never searches by name.
/// </summary>
public sealed class CoverArtArchiveSource : IArtworkSource
{
    private const string Api = "https://coverartarchive.org/";

    /// <inheritdoc />
    public string Id => SourceIds.CoverArtArchive;

    /// <summary>
    /// Parses an archive listing into candidates.
    /// </summary>
    /// <param name="root">The response.</param>
    /// <returns>The candidates.</returns>
    public static List<ArtworkCandidate> Parse(JsonElement root)
    {
        var list = new List<ArtworkCandidate>();
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var img in images.EnumerateArray())
        {
            var front = img.TryGetProperty("front", out var f) && f.ValueKind == JsonValueKind.True;
            if (!front)
            {
                continue;
            }

            // Prefer the 1200 px rendition: the original can be many megabytes of scan.
            string? url = null;
            if (img.TryGetProperty("thumbnails", out var t) && t.ValueKind == JsonValueKind.Object)
            {
                url = SourceHelpers.Str(t, "1200") ?? SourceHelpers.Str(t, "large") ?? SourceHelpers.Str(t, "500");
            }

            url ??= SourceHelpers.Str(img, "image");
            if (!SourceHelpers.TryHttps(url, out var uri))
            {
                continue;
            }

            list.Add(new ArtworkCandidate
            {
                Source = SourceIds.CoverArtArchive,
                RemoteId = img.TryGetProperty("id", out var id) ? id.ToString() : null,
                Uri = uri,
                ImageType = ImageType.Primary,
                Score = 2,
                Attribution = new ArtworkAttribution(null, "Cover Art Archive (see the release page for rights)", "https://musicbrainz.org/doc/Cover_Art_Archive/API", null)
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
        if (query.Kind != BaseItemKind.MusicAlbum || !query.Wants(ImageType.Primary))
        {
            return [];
        }

        string? path = query.Id("MusicBrainzReleaseGroup") is { } rg
            ? "release-group/" + rg
            : query.Id("MusicBrainzAlbum") is { } rel ? "release/" + rel : null;
        if (path is null)
        {
            return [];
        }

        using var doc = await http.GetJsonAsync(SourceIds.CoverArtArchive, new Uri(Api + path), configuration.Sources.CoverArtArchive.RequestsPerMinute, null, cancellationToken).ConfigureAwait(false);
        return doc is null ? [] : Parse(doc.RootElement);
    }
}

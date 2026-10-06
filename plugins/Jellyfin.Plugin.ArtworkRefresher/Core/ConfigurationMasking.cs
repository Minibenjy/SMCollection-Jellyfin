using System;
using System.Linq;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;

namespace Jellyfin.Plugin.ArtworkRefresher.Core;

/// <summary>
/// Keeps API keys away from the browser. On the way out a key becomes the mask; on the way back
/// the mask means "keep the stored key", an empty value means "delete it" and anything else
/// replaces it. The mask is never stored.
/// </summary>
public static class ConfigurationMasking
{
    /// <summary>The value shown in place of a stored key.</summary>
    public const string Mask = "***";

    /// <summary>
    /// Copies the sources with their keys masked.
    /// </summary>
    /// <param name="sources">The stored sources.</param>
    /// <returns>A masked copy.</returns>
    public static SourceConfiguration MaskedCopy(SourceConfiguration sources)
    {
        var copy = Copy(sources);
        foreach (var s in All(copy))
        {
            s.ApiKey = s.ApiKey.Length > 0 ? Mask : string.Empty;
            s.ApiKey2 = s.ApiKey2.Length > 0 ? Mask : string.Empty;
        }

        return copy;
    }

    /// <summary>
    /// Applies incoming sources over the stored ones, resolving the mask rule for each key.
    /// </summary>
    /// <param name="stored">The stored sources.</param>
    /// <param name="incoming">The sources from the page.</param>
    /// <returns>The sources to store.</returns>
    public static SourceConfiguration Merge(SourceConfiguration stored, SourceConfiguration incoming)
    {
        var result = Copy(incoming);
        var oldList = All(stored);
        var newList = All(result);
        for (var i = 0; i < newList.Length; i++)
        {
            newList[i].ApiKey = Resolve(oldList[i].ApiKey, newList[i].ApiKey);
            newList[i].ApiKey2 = Resolve(oldList[i].ApiKey2, newList[i].ApiKey2);
        }

        return result;
    }

    private static string Resolve(string stored, string incoming)
        => incoming == Mask ? stored : (incoming ?? string.Empty).Trim();

    private static SourceSettings[] All(SourceConfiguration c)
        => [c.Tmdb, c.Fanart, c.Wikimedia, c.OpenLibrary, c.CoverArtArchive, c.GoogleCse];

    private static SourceConfiguration Copy(SourceConfiguration s)
    {
        SourceSettings Clone(SourceSettings x) => new()
        {
            Enabled = x.Enabled,
            ApiKey = x.ApiKey ?? string.Empty,
            ApiKey2 = x.ApiKey2 ?? string.Empty,
            RequestsPerMinute = x.RequestsPerMinute
        };

        return new SourceConfiguration
        {
            Tmdb = Clone(s.Tmdb),
            Fanart = Clone(s.Fanart),
            Wikimedia = Clone(s.Wikimedia),
            OpenLibrary = Clone(s.OpenLibrary),
            CoverArtArchive = Clone(s.CoverArtArchive),
            GoogleCse = Clone(s.GoogleCse),
            GoogleSafeSearch = s.GoogleSafeSearch,
            GoogleRightsFilter = s.GoogleRightsFilter ?? string.Empty,
            TmdbPosterSize = s.TmdbPosterSize ?? "w780",
            TmdbBackdropSize = s.TmdbBackdropSize ?? "w1280",
            TmdbLogoSize = s.TmdbLogoSize ?? "w500"
        };
    }
}

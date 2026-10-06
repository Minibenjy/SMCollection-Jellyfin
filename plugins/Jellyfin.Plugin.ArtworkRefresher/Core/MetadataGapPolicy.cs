using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;

namespace Jellyfin.Plugin.ArtworkRefresher.Core;

/// <summary>What an item has today. Only what the gap policy needs, so it can be tested without a server.</summary>
public sealed class ItemMetadataSnapshot
{
    /// <summary>Gets the item kind.</summary>
    public required BaseItemKind Kind { get; init; }

    /// <summary>Gets the ids of the libraries the item belongs to.</summary>
    public Guid[] LibraryIds { get; init; } = [];

    /// <summary>Gets a value indicating whether Jellyfin's own lock is on.</summary>
    public bool NativeLocked { get; init; }

    /// <summary>Gets the names of the fields locked in Jellyfin (Overview, Genres, Studios...).</summary>
    public IReadOnlyCollection<string> LockedFields { get; init; } = [];

    /// <summary>Gets the overview.</summary>
    public string? Overview { get; init; }

    /// <summary>Gets the genres.</summary>
    public IReadOnlyCollection<string> Genres { get; init; } = [];

    /// <summary>Gets the production year.</summary>
    public int? ProductionYear { get; init; }

    /// <summary>Gets the community rating.</summary>
    public float? CommunityRating { get; init; }

    /// <summary>Gets the studios.</summary>
    public IReadOnlyCollection<string> Studios { get; init; } = [];

    /// <summary>Gets the taglines.</summary>
    public IReadOnlyCollection<string> Taglines { get; init; } = [];
}

/// <summary>What a metadata source knows about an item.</summary>
public sealed class RemoteMetadata
{
    /// <summary>Gets or sets the overview (biography for people).</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the genres.</summary>
    public string[] Genres { get; set; } = [];

    /// <summary>Gets or sets the production year.</summary>
    public int? ProductionYear { get; set; }

    /// <summary>Gets or sets the community rating (0 to 10).</summary>
    public float? CommunityRating { get; set; }

    /// <summary>Gets or sets the studios.</summary>
    public string[] Studios { get; set; } = [];

    /// <summary>Gets or sets the tagline.</summary>
    public string? Tagline { get; set; }
}

/// <summary>One field to fill and its value.</summary>
/// <param name="Field">The field name.</param>
/// <param name="Value">The value: a string, string[], int or float.</param>
public sealed record MetadataFill(string Field, object Value);

/// <summary>
/// Decides which empty metadata fields may be filled. It only ever fills: a field that has a value is
/// never overwritten, a field locked in Jellyfin is never touched, books are left alone unless their library is
/// explicitly opted in.
/// </summary>
public static class MetadataGapPolicy
{
    /// <summary>Overview field.</summary>
    public const string Overview = "Overview";

    /// <summary>Genres field.</summary>
    public const string Genres = "Genres";

    /// <summary>Production year field.</summary>
    public const string ProductionYear = "ProductionYear";

    /// <summary>Community rating field.</summary>
    public const string CommunityRating = "CommunityRating";

    /// <summary>Studios field.</summary>
    public const string Studios = "Studios";

    /// <summary>Tagline field.</summary>
    public const string Tagline = "Tagline";

    /// <summary>All the fields that can be filled.</summary>
    public static readonly string[] AllFields = [Overview, Genres, ProductionYear, CommunityRating, Studios, Tagline];

    /// <summary>The kinds whose metadata is looked up at TMDb.</summary>
    public static readonly BaseItemKind[] SupportedKinds = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.Person];

    /// <summary>The kinds that are never filled unless the library is opted in.</summary>
    public static readonly BaseItemKind[] BookKinds = [BaseItemKind.Book, BaseItemKind.AudioBook];

    /// <summary>Tells whether an item may be looked at for gaps at all.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="item">The item.</param>
    /// <returns>True when it may.</returns>
    public static bool IsEligible(PluginConfiguration configuration, ItemMetadataSnapshot item)
    {
        if (item.LibraryIds.Any(id => configuration.ExcludedLibraryIds.Contains(id)))
        {
            return false;
        }

        if (configuration.IncludedLibraryIds.Length > 0 && item.Kind != BaseItemKind.Person
            && !item.LibraryIds.Any(id => configuration.IncludedLibraryIds.Contains(id)))
        {
            return false;
        }

        if (BookKinds.Contains(item.Kind))
        {
            // Books, comics and magazines have local metadata decided elsewhere: only an explicit per-library opt-in.
            return item.LibraryIds.Any(id => configuration.MetadataBookLibraryOptIn.Contains(id)) && SupportedKinds.Contains(item.Kind);
        }

        if (configuration.RespectNativeLockData && item.NativeLocked)
        {
            return false;
        }

        return SupportedKinds.Contains(item.Kind);
    }

    /// <summary>Lists the fields that are empty, allowed by the settings and not locked.</summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="item">The item.</param>
    /// <returns>The field names with a gap.</returns>
    public static IReadOnlyList<string> Gaps(PluginConfiguration configuration, ItemMetadataSnapshot item)
    {
        var gaps = new List<string>();
        if (!IsEligible(configuration, item))
        {
            return gaps;
        }

        bool Wanted(string f) => (configuration.MetadataFieldsToFill.Length == 0
                                  || configuration.MetadataFieldsToFill.Contains(f, StringComparer.OrdinalIgnoreCase))
                                 && !item.LockedFields.Contains(f, StringComparer.OrdinalIgnoreCase);

        var person = item.Kind == BaseItemKind.Person;
        if (string.IsNullOrWhiteSpace(item.Overview) && Wanted(Overview))
        {
            gaps.Add(Overview);
        }

        if (person)
        {
            return gaps;
        }

        if (item.Genres.Count == 0 && item.Kind != BaseItemKind.Episode && Wanted(Genres))
        {
            gaps.Add(Genres);
        }

        if (item.ProductionYear is null or <= 0 && Wanted(ProductionYear))
        {
            gaps.Add(ProductionYear);
        }

        if ((item.CommunityRating is null or <= 0) && Wanted(CommunityRating))
        {
            gaps.Add(CommunityRating);
        }

        if (item.Studios.Count == 0 && item.Kind != BaseItemKind.Episode && Wanted(Studios))
        {
            gaps.Add(Studios);
        }

        if (item.Taglines.Count == 0 && (item.Kind is BaseItemKind.Movie or BaseItemKind.Series) && Wanted(Tagline))
        {
            gaps.Add(Tagline);
        }

        return gaps;
    }

    /// <summary>Chooses what to write: for each gap, the remote value when it has one.</summary>
    /// <param name="gaps">The gaps found by <see cref="Gaps"/>.</param>
    /// <param name="remote">What the source knows.</param>
    /// <returns>The fills.</returns>
    public static IReadOnlyList<MetadataFill> Plan(IReadOnlyList<string> gaps, RemoteMetadata remote)
    {
        var fills = new List<MetadataFill>();
        foreach (var gap in gaps)
        {
            switch (gap)
            {
                case Overview when !string.IsNullOrWhiteSpace(remote.Overview):
                    fills.Add(new MetadataFill(gap, remote.Overview.Trim()));
                    break;
                case Genres when remote.Genres.Length > 0:
                    fills.Add(new MetadataFill(gap, remote.Genres));
                    break;
                case ProductionYear when remote.ProductionYear is > 0:
                    fills.Add(new MetadataFill(gap, remote.ProductionYear.Value));
                    break;
                case CommunityRating when remote.CommunityRating is > 0:
                    fills.Add(new MetadataFill(gap, remote.CommunityRating.Value));
                    break;
                case Studios when remote.Studios.Length > 0:
                    fills.Add(new MetadataFill(gap, remote.Studios));
                    break;
                case Tagline when !string.IsNullOrWhiteSpace(remote.Tagline):
                    fills.Add(new MetadataFill(gap, remote.Tagline.Trim()));
                    break;
            }
        }

        return fills;
    }
}

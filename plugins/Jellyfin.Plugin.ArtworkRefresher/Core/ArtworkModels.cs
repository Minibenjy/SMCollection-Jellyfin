using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ArtworkRefresher.Core;

/// <summary>
/// The place an image occupies on an item: a type and, for types that can repeat, an index.
/// </summary>
/// <param name="Type">The image type.</param>
/// <param name="Index">The index, 0 for the first image of the type.</param>
public readonly record struct ImageSlot(ImageType Type, int Index)
{
    /// <summary>Gets the key used in the state file, for example Primary:0.</summary>
    public string Key => Type + ":" + Index;
}

/// <summary>
/// Where an image candidate comes from, for attribution and audit.
/// </summary>
/// <param name="Author">The author or rights holder, when known.</param>
/// <param name="License">The licence name, when known.</param>
/// <param name="LicenseUrl">A link to the licence, when known.</param>
/// <param name="PageUrl">A link to the page of the image, when known.</param>
public sealed record ArtworkAttribution(string? Author, string? License, string? LicenseUrl, string? PageUrl);

/// <summary>
/// An image a source offers. Searching never downloads: that is what makes a dry run possible.
/// </summary>
public sealed class ArtworkCandidate
{
    /// <summary>Gets the source id.</summary>
    public required string Source { get; init; }

    /// <summary>Gets the id of the image at the source.</summary>
    public string? RemoteId { get; init; }

    /// <summary>Gets the https address of the image.</summary>
    public required Uri Uri { get; init; }

    /// <summary>Gets the image type the candidate fits.</summary>
    public required ImageType ImageType { get; init; }

    /// <summary>Gets the width, when the source says.</summary>
    public int? Width { get; init; }

    /// <summary>Gets the height, when the source says.</summary>
    public int? Height { get; init; }

    /// <summary>Gets the ISO 639-1 language of the image, or null when it has no text.</summary>
    public string? Language { get; init; }

    /// <summary>Gets the ranking score, higher is better.</summary>
    public double Score { get; init; }

    /// <summary>Gets a value indicating whether the host may be any public host (search engines).</summary>
    public bool AnyPublicHost { get; init; }

    /// <summary>Gets the attribution.</summary>
    public ArtworkAttribution? Attribution { get; init; }
}

/// <summary>
/// What a source needs to know about an item to look for its artwork.
/// </summary>
public sealed class ArtworkQuery
{
    /// <summary>Gets the item kind.</summary>
    public required BaseItemKind Kind { get; init; }

    /// <summary>Gets the item name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the production year, when known.</summary>
    public int? Year { get; init; }

    /// <summary>Gets the provider ids of the item.</summary>
    public IReadOnlyDictionary<string, string> ProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the provider ids of the parent series (for seasons and episodes).</summary>
    public IReadOnlyDictionary<string, string> SeriesProviderIds { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the season number (seasons and episodes).</summary>
    public int? SeasonNumber { get; init; }

    /// <summary>Gets the episode number (episodes).</summary>
    public int? EpisodeNumber { get; init; }

    /// <summary>Gets the name of the artist or author, when known.</summary>
    public string? CreatorName { get; init; }

    /// <summary>Gets the image types wanted.</summary>
    public IReadOnlyList<ImageType> WantedTypes { get; init; } = [];

    /// <summary>Gets the preferred languages, best first.</summary>
    public IReadOnlyList<string> Languages { get; init; } = [];

    /// <summary>Gets a value indicating whether language-neutral images are accepted.</summary>
    public bool IncludeNeutral { get; init; } = true;

    /// <summary>Tells whether an image type is wanted.</summary>
    /// <param name="type">The type.</param>
    /// <returns>True when wanted.</returns>
    public bool Wants(ImageType type)
    {
        foreach (var t in WantedTypes)
        {
            if (t == type)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Gets a provider id or null.</summary>
    /// <param name="key">The provider name.</param>
    /// <returns>The id.</returns>
    public string? Id(string key)
        => ProviderIds.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    /// <summary>Gets a parent series provider id or null.</summary>
    /// <param name="key">The provider name.</param>
    /// <returns>The id.</returns>
    public string? SeriesId(string key)
        => SeriesProviderIds.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}

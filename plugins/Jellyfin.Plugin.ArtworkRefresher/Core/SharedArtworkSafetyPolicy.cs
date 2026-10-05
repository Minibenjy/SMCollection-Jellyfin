using System;
using System.Linq;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;

namespace Jellyfin.Plugin.ArtworkRefresher.Core;

/// <summary>
/// What the policy needs to know about an item that might go in a shared image.
/// </summary>
/// <param name="LibraryIds">The libraries the item is in.</param>
/// <param name="Tags">The item's tags.</param>
/// <param name="ParentalRatingValue">The inherited parental rating value, null when unrated.</param>
public sealed record SharedArtworkSubject(Guid[] LibraryIds, string[] Tags, int? ParentalRatingValue);

/// <summary>
/// A genre, a studio or a collection has one image for every user, so it may only be built
/// from content that is safe for everyone: the conservative set the administrator defines. The
/// policy never reads another plugin's private rules (Kids, Mature); the administrator lists
/// the libraries and tags there.
/// </summary>
public static class SharedArtworkSafetyPolicy
{
    /// <summary>
    /// Tells whether an item may appear in an image shared by all users.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="subject">The item.</param>
    /// <returns>True when allowed.</returns>
    public static bool IsAllowed(PluginConfiguration configuration, SharedArtworkSubject subject)
    {
        if (!configuration.SharedArtworkSafetyEnabled)
        {
            return true;
        }

        if (subject.LibraryIds.Any(id => configuration.SensitiveLibraryIds.Contains(id)))
        {
            return false;
        }

        if (subject.Tags.Any(t => configuration.SensitiveTags.Contains(t, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (subject.ParentalRatingValue is null)
        {
            return !configuration.ExcludeUnratedFromSharedArtwork;
        }

        return configuration.MaximumSharedParentalRatingValue < 0
               || subject.ParentalRatingValue.Value <= configuration.MaximumSharedParentalRatingValue;
    }
}

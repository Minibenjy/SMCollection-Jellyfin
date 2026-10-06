using System;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ArtworkRefresher.Core;

/// <summary>
/// The outcome of asking whether an image slot may be changed.
/// </summary>
public enum LockDecision
{
    /// <summary>The slot may be changed.</summary>
    Allowed = 0,

    /// <summary>The item is not in an included library.</summary>
    NotIncluded,

    /// <summary>The item is in an excluded library.</summary>
    ExcludedLibrary,

    /// <summary>The item carries Jellyfin's own lock.</summary>
    NativeLock,

    /// <summary>A library lock covers the slot.</summary>
    LibraryLock,

    /// <summary>An item lock covers the slot.</summary>
    ItemLock,

    /// <summary>The primary image of a book (or of an Auto Thumbnails item) is protected.</summary>
    BookPrimaryProtected,

    /// <summary>This plugin does not handle that item kind.</summary>
    UnsupportedKind,

    /// <summary>That image type is switched off.</summary>
    UnsupportedType,

    /// <summary>The slot already has an image and the mode is missing-only.</summary>
    AlreadyHasImage
}

/// <summary>
/// What the policy needs to know about the item and the request.
/// </summary>
public sealed class LockContext
{
    /// <summary>Gets the item kind.</summary>
    public required BaseItemKind Kind { get; init; }

    /// <summary>Gets the item id.</summary>
    public required Guid ItemId { get; init; }

    /// <summary>Gets the ids of the libraries the item belongs to (empty for categories).</summary>
    public Guid[] LibraryIds { get; init; } = [];

    /// <summary>Gets a value indicating whether the item has Jellyfin's native lock.</summary>
    public bool NativeLocked { get; init; }

    /// <summary>Gets a value indicating whether the item carries the Auto Thumbnails marker.</summary>
    public bool HasAutoThumbnailsMarker { get; init; }

    /// <summary>Gets the slot being asked about.</summary>
    public ImageSlot Slot { get; init; }

    /// <summary>Gets a value indicating whether the slot already holds an image.</summary>
    public bool SlotHasImage { get; init; }

    /// <summary>Gets a value indicating whether the request is an administrator's manual force.</summary>
    public bool Force { get; init; }

    /// <summary>Gets a value indicating whether a forced request may also enter excluded libraries.</summary>
    public bool OverrideExcludedLibraries { get; init; }

    /// <summary>Gets a value indicating whether the item kind is a category (no library membership).</summary>
    public bool IsCategory { get; init; }
}

/// <summary>
/// Decides whether this plugin may change an image slot. The order of the checks is the contract:
/// excluded library, native lock, library lock, item lock, book primary protection, unsupported
/// kind or type, missing-only test.
/// </summary>
public static class ImageLockPolicy
{
    /// <summary>The kinds this plugin handles.</summary>
    public static readonly BaseItemKind[] SupportedKinds =
    [
        BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode,
        BaseItemKind.MusicAlbum, BaseItemKind.MusicArtist, BaseItemKind.Person,
        BaseItemKind.Genre, BaseItemKind.MusicGenre, BaseItemKind.Studio, BaseItemKind.BoxSet,
        BaseItemKind.Book
    ];

    /// <summary>
    /// Evaluates the policy.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="context">The item and request.</param>
    /// <param name="mode">Missing-only or replace.</param>
    /// <returns>The decision.</returns>
    public static LockDecision Evaluate(PluginConfiguration configuration, LockContext context, ArtworkRefreshMode mode)
    {
        if (!context.IsCategory)
        {
            if (context.LibraryIds.Any(id => configuration.ExcludedLibraryIds.Contains(id)) && !(context.Force && context.OverrideExcludedLibraries))
            {
                return LockDecision.ExcludedLibrary;
            }

            if (configuration.IncludedLibraryIds.Length > 0
                && !context.LibraryIds.Any(id => configuration.IncludedLibraryIds.Contains(id))
                && !(context.Force && context.OverrideExcludedLibraries))
            {
                return LockDecision.NotIncluded;
            }
        }

        if (configuration.RespectNativeLockData && context.NativeLocked)
        {
            return LockDecision.NativeLock;
        }

        var typeName = context.Slot.Type.ToString();
        bool Matches(ImageLockRule rule)
            => string.IsNullOrEmpty(rule.ImageType)
               || string.Equals(rule.ImageType, typeName, StringComparison.OrdinalIgnoreCase);

        if (configuration.ImageLocks.Any(r => r.IsLibrary && Matches(r) && context.LibraryIds.Contains(r.TargetId)))
        {
            return LockDecision.LibraryLock;
        }

        if (!context.Force && configuration.ImageLocks.Any(r => !r.IsLibrary && Matches(r) && r.TargetId == context.ItemId))
        {
            return LockDecision.ItemLock;
        }

        if (context.Slot.Type == ImageType.Primary
            && !context.Force
            && ((configuration.ProtectBookPrimary && context.Kind == BaseItemKind.Book)
                || (configuration.RespectAutoThumbnailsMarker && context.HasAutoThumbnailsMarker)))
        {
            return LockDecision.BookPrimaryProtected;
        }

        if (!SupportedKinds.Contains(context.Kind))
        {
            return LockDecision.UnsupportedKind;
        }

        if (!configuration.EnabledImageTypes.Contains(context.Slot.Type))
        {
            return LockDecision.UnsupportedType;
        }

        if (mode == ArtworkRefreshMode.MissingOnly && context.SlotHasImage && !context.Force)
        {
            return LockDecision.AlreadyHasImage;
        }

        return LockDecision.Allowed;
    }
}

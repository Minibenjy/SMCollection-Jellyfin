using System;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ArtworkRefresher.Configuration;

/// <summary>
/// How an existing image is treated.
/// </summary>
public enum ArtworkRefreshMode
{
    /// <summary>Only fill image slots that are empty.</summary>
    MissingOnly = 0,

    /// <summary>Replace the image in a slot when a better candidate is found.</summary>
    Replace = 1
}

/// <summary>
/// Credentials and switches for one remote source.
/// </summary>
public class SourceSettings
{
    /// <summary>Gets or sets a value indicating whether the source is used.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the API key or token. Never returned to the browser.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets a second credential (Fanart.tv client key, Google search engine id).</summary>
    public string ApiKey2 { get; set; } = string.Empty;

    /// <summary>Gets or sets the request budget per minute for this source.</summary>
    public int RequestsPerMinute { get; set; } = 30;
}

/// <summary>
/// The settings of every source, by name.
/// </summary>
public class SourceConfiguration
{
    /// <summary>Gets or sets TMDb.</summary>
    public SourceSettings Tmdb { get; set; } = new() { Enabled = false, RequestsPerMinute = 40 };

    /// <summary>Gets or sets Fanart.tv.</summary>
    public SourceSettings Fanart { get; set; } = new() { Enabled = false, RequestsPerMinute = 20 };

    /// <summary>Gets or sets Wikimedia Commons (no key).</summary>
    public SourceSettings Wikimedia { get; set; } = new() { Enabled = true, RequestsPerMinute = 30 };

    /// <summary>Gets or sets Open Library (no key).</summary>
    public SourceSettings OpenLibrary { get; set; } = new() { Enabled = true, RequestsPerMinute = 20 };

    /// <summary>Gets or sets Cover Art Archive (no key).</summary>
    public SourceSettings CoverArtArchive { get; set; } = new() { Enabled = true, RequestsPerMinute = 20 };

    /// <summary>Gets or sets Google Programmable Search. Off by default.</summary>
    public SourceSettings GoogleCse { get; set; } = new() { Enabled = false, RequestsPerMinute = 10 };

    /// <summary>Gets or sets a value indicating whether Google results use SafeSearch.</summary>
    public bool GoogleSafeSearch { get; set; } = true;

    /// <summary>Gets or sets the optional Google rights filter (for example cc_publicdomain).</summary>
    public string GoogleRightsFilter { get; set; } = string.Empty;

    /// <summary>Gets or sets the TMDb image size for posters (a size token such as w780 or original).</summary>
    public string TmdbPosterSize { get; set; } = "w780";

    /// <summary>Gets or sets the TMDb image size for backdrops.</summary>
    public string TmdbBackdropSize { get; set; } = "w1280";

    /// <summary>Gets or sets the TMDb image size for logos.</summary>
    public string TmdbLogoSize { get; set; } = "w500";
}

/// <summary>
/// Source order for one kind of item. An empty list means the built-in default.
/// </summary>
public class SourceOrderEntry
{
    /// <summary>Gets or sets the item kind (a BaseItemKind name such as Movie or Genre).</summary>
    public string ItemKind { get; set; } = string.Empty;

    /// <summary>Gets or sets the source ids in order: Tmdb, Fanart, Wikimedia, OpenLibrary, CoverArtArchive, GoogleCse, LocalMosaic.</summary>
    public string[] Sources { get; set; } = [];
}

/// <summary>
/// An image lock. A lock on a library or an item stops this plugin from changing
/// the given image type, or every type when the type is empty.
/// </summary>
public class ImageLockRule
{
    /// <summary>Gets or sets the library or item id.</summary>
    public Guid TargetId { get; set; }

    /// <summary>Gets or sets a value indicating whether the target is a library (otherwise an item).</summary>
    public bool IsLibrary { get; set; }

    /// <summary>Gets or sets the image type name, or empty for every type.</summary>
    public string ImageType { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional note shown on the configuration page.</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// Configuration of the Artwork Refresher plugin. Policy and credentials only: the
/// per-item state lives in the plugin's own state file.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether scheduled runs do anything.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the hours between two complete runs.</summary>
    public int RefreshIntervalHours { get; set; } = 24;

    /// <summary>Gets or sets a value indicating whether runs are limited to a time window.</summary>
    public bool RefreshWindowEnabled { get; set; } = true;

    /// <summary>Gets or sets the start of the window, HH:mm, in the time zone below.</summary>
    public string RefreshWindowStart { get; set; } = "02:00";

    /// <summary>Gets or sets the end of the window, HH:mm. May be earlier than the start (crosses midnight).</summary>
    public string RefreshWindowEnd { get; set; } = "06:00";

    /// <summary>Gets or sets the time zone id of the window. Empty means the server's local time zone.</summary>
    public string RefreshWindowTimeZone { get; set; } = string.Empty;

    /// <summary>Gets or sets the longest a run may last, in minutes. 0 means no limit besides the window.</summary>
    public int MaxRunMinutes { get; set; } = 180;

    /// <summary>Gets or sets the minutes between scheduler checks (a hint for the default trigger).</summary>
    public int SchedulerHeartbeatMinutes { get; set; } = 30;

    /// <summary>Gets or sets whether existing images are kept or replaced.</summary>
    public ArtworkRefreshMode RefreshMode { get; set; } = ArtworkRefreshMode.MissingOnly;

    /// <summary>Gets or sets the image types handled.</summary>
    public ImageType[] EnabledImageTypes { get; set; } = [ImageType.Primary, ImageType.Backdrop, ImageType.Logo, ImageType.Thumb];

    /// <summary>Gets or sets the libraries covered. Empty means all libraries.</summary>
    public Guid[] IncludedLibraryIds { get; set; } = [];

    /// <summary>Gets or sets the libraries never touched.</summary>
    public Guid[] ExcludedLibraryIds { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether items with Jellyfin's own lock are left alone.</summary>
    public bool RespectNativeLockData { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the primary image of a book is protected.</summary>
    public bool ProtectBookPrimary { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether items carrying the Auto Thumbnails marker keep their primary image.</summary>
    public bool RespectAutoThumbnailsMarker { get; set; } = true;

    /// <summary>Gets or sets the image locks.</summary>
    public ImageLockRule[] ImageLocks { get; set; } = [];

    /// <summary>Gets or sets the preferred image languages (ISO 639-1), best first. Empty means the server metadata language.</summary>
    public string[] PreferredLanguages { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether images without text (no language) are accepted.</summary>
    public bool IncludeLanguageNeutralImages { get; set; } = true;

    /// <summary>Gets or sets the smallest accepted image width.</summary>
    public int MinimumImageWidth { get; set; } = 300;

    /// <summary>Gets or sets the smallest accepted image height.</summary>
    public int MinimumImageHeight { get; set; } = 150;

    /// <summary>Gets or sets the largest accepted download, in bytes.</summary>
    public long MaximumDownloadBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>Gets or sets the HTTP timeout in seconds.</summary>
    public int HttpTimeoutSeconds { get; set; } = 30;

    /// <summary>Gets or sets the most redirects followed.</summary>
    public int MaximumRedirects { get; set; } = 3;

    /// <summary>Gets or sets a value indicating whether images shared by every user (genres, studios, mosaics) use only safe content.</summary>
    public bool SharedArtworkSafetyEnabled { get; set; } = true;

    /// <summary>Gets or sets libraries whose items never appear in a shared image.</summary>
    public Guid[] SensitiveLibraryIds { get; set; } = [];

    /// <summary>Gets or sets tags whose items never appear in a shared image.</summary>
    public string[] SensitiveTags { get; set; } = [];

    /// <summary>Gets or sets the highest parental rating value allowed in a shared image. Negative means no limit.</summary>
    public int MaximumSharedParentalRatingValue { get; set; } = -1;

    /// <summary>Gets or sets a value indicating whether unrated items stay out of shared images.</summary>
    public bool ExcludeUnratedFromSharedArtwork { get; set; } = true;

    /// <summary>Gets or sets whether genres, studios, collections and Home sections get a local mosaic.</summary>
    public bool MosaicEnabled { get; set; } = true;

    /// <summary>Gets or sets the fewest items a category needs before it gets a mosaic.</summary>
    public int MosaicMinimumItems { get; set; } = 2;

    /// <summary>Gets or sets a value indicating whether mosaics are re-chosen every rotation period.</summary>
    public bool RotateMosaics { get; set; }

    /// <summary>Gets or sets the days between mosaic rotations (only when rotation is on).</summary>
    public int MosaicRotationDays { get; set; } = 30;

    /// <summary>Gets or sets a value indicating whether people (actors) are refreshed too, after everything else.</summary>
    public bool IncludePeople { get; set; } = true;

    /// <summary>Gets or sets how many alternative images Replace mode may cycle through.</summary>
    public int ReplaceRotationPool { get; set; } = 3;

    /// <summary>Gets or sets the mosaic columns.</summary>
    public int MosaicColumns { get; set; } = 2;

    /// <summary>Gets or sets the mosaic rows.</summary>
    public int MosaicRows { get; set; } = 2;

    /// <summary>Gets or sets the mosaic width in pixels.</summary>
    public int MosaicWidth { get; set; } = 1000;

    /// <summary>Gets or sets the mosaic height in pixels.</summary>
    public int MosaicHeight { get; set; } = 1000;

    /// <summary>Gets or sets the JPEG quality of generated images.</summary>
    public int JpegQuality { get; set; } = 90;

    /// <summary>Gets or sets a value indicating whether runs only log what they would do.</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets a value indicating whether non-administrators may refresh one item they can see.</summary>
    public bool AllowNonAdminItemRefresh { get; set; }

    /// <summary>Gets or sets the minutes before a non-administrator may refresh the same item again.</summary>
    public int NonAdminRefreshCooldownMinutes { get; set; } = 60;

    /// <summary>Gets or sets days before an item with no candidate is tried again.</summary>
    public int RetryAfterDays { get; set; } = 7;

    /// <summary>Gets or sets the sources.</summary>
    public SourceConfiguration Sources { get; set; } = new();

    /// <summary>Gets or sets per-kind source orders. Empty means the built-in order.</summary>
    public SourceOrderEntry[] SourceOrder { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the administrator-supplied extra hosts are honoured.</summary>
    public bool AllowCustomProviderHosts { get; set; }

    /// <summary>Gets or sets additional download hosts (only used when custom hosts are allowed).</summary>
    public string[] AdditionalAllowedHosts { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the client script is added to the web client.</summary>
    public bool InjectClientScript { get; set; } = true;
}

using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DiscoverHome.Configuration;

/// <summary>
/// Administrator-facing settings for the DiscoverHome layer.
/// </summary>
/// <remarks>
/// Everything here is presentation. The plugin never changes what a user is allowed
/// to see — it only reorders and restyles what Jellyfin already rendered for them —
/// so none of these values carry a permission decision.
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether the client script is added to the web client.</summary>
    /// <remarks>Turning this off disables the whole feature without uninstalling the plugin.</remarks>
    public bool InjectClientScript { get; set; } = true;

    // ---------------------------------------------------------------- layout

    /// <summary>Gets or sets a value indicating whether the navigation drawer stays open on desktop.</summary>
    public bool PinSidebar { get; set; } = true;

    /// <summary>Gets or sets the viewport width, in pixels, at or above which the sidebar is pinned.</summary>
    public int SidebarBreakpoint { get; set; } = 1000;

    /// <summary>Gets or sets a value indicating whether the sidebar's nav groups are reordered (libraries first).</summary>
    public bool ReorderSidebar { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the logo is moved into the sidebar.</summary>
    public bool LogoInSidebar { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a centered search box replaces the header's nav tabs.</summary>
    public bool CenteredSearch { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether Jellyfin Enhanced's random-item button is hidden.</summary>
    public bool HideRandomButton { get; set; } = true;

    // ---------------------------------------------------------------- cards

    /// <summary>Gets or sets a value indicating whether home-screen poster cards are made denser.</summary>
    public bool CompactCards { get; set; } = true;

    /// <summary>Gets or sets the width, in pixels, of a compacted poster card.</summary>
    public int CardWidth { get; set; } = 130;

    /// <summary>
    /// Gets or sets a value indicating whether, above the sidebar breakpoint, poster cards
    /// and the genre/studio tiles are scaled to Jellyfin's own landscape cards
    /// (Continue Watching, My Media) instead of <see cref="CardWidth"/>.
    /// </summary>
    public bool LargeDesktopCards { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a media-type pill is drawn on each card.</summary>
    public bool ShowTypeBadges { get; set; } = true;

    /// <summary>Gets or sets the accent colour used for highlights, badges and the active nav row.</summary>
    public string AccentColor { get; set; } = "#7c5cff";

    /// <summary>Gets or sets the badge text for a movie.</summary>
    /// <remarks>
    /// The badges are drawn entirely in CSS from these values, so that a page full of
    /// cards costs no per-card JavaScript. They are plain settings rather than
    /// translations because the wording is a design choice as much as a language one.
    /// </remarks>
    public string LabelMovie { get; set; } = "PELÍCULA";

    /// <summary>Gets or sets the badge text for a series.</summary>
    public string LabelSeries { get; set; } = "SERIE";

    /// <summary>Gets or sets the badge text for an episode.</summary>
    public string LabelEpisode { get; set; } = "EPISODIO";

    // ---------------------------------------------------------------- rows

    /// <summary>Gets or sets a value indicating whether home-screen rows are shuffled on each load.</summary>
    public bool ShuffleSections { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether genre carousels are inserted between native rows.</summary>
    public bool EnableGenreRow { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether studio carousels are inserted between native rows.</summary>
    public bool EnableStudioRow { get; set; } = true;

    /// <summary>Gets or sets the smallest number of native rows between two inserted carousels.</summary>
    public int MinRowGap { get; set; } = 1;

    /// <summary>Gets or sets the largest number of native rows between two inserted carousels.</summary>
    public int MaxRowGap { get; set; } = 2;

    /// <summary>Gets or sets how many tiles an inserted carousel shows.</summary>
    public int ChannelRowSize { get; set; } = 8;

    // ---------------------------------------------------------------- artwork

    /// <summary>Gets or sets a value indicating whether studio tiles use downloaded logos.</summary>
    public bool EnableStudioLogos { get; set; } = true;

    /// <summary>
    /// Gets or sets the base URL of the public studio artwork repository.
    /// </summary>
    /// <remarks>
    /// Defaults to the same community artwork repo Jellyfin's own Studio Images plugin
    /// uses. The layout expected is <c>{base}/thumbs.txt</c> for the index and
    /// <c>{base}/images/{studio}/thumb.jpg</c> for each logo.
    /// </remarks>
    public string StudioArtworkRepository { get; set; }
        = "https://raw.githubusercontent.com/jellyfin/emby-artwork/master/studios";

    /// <summary>
    /// Gets or sets a value indicating whether genre tiles are backed by a collage of
    /// posters drawn from that genre.
    /// </summary>
    /// <remarks>
    /// Composed in the browser from images this server already serves, so unlike studio
    /// logos there is nothing to download and nothing to keep in sync.
    /// </remarks>
    public bool EnableGenreCollages { get; set; } = true;

    /// <summary>Gets or sets the UTC timestamp of the last successful artwork sync, as a round-trip string.</summary>
    public string LastArtworkSyncUtc { get; set; } = string.Empty;

    /// <summary>Gets or sets the number of logos held in the cache at the last sync.</summary>
    public int CachedLogoCount { get; set; }
}

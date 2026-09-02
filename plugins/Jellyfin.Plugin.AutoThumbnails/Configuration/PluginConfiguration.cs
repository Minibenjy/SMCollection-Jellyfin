using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AutoThumbnails.Configuration;

/// <summary>
/// Configuration for the Auto Thumbnails plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether covers are extracted from books,
    /// comics and magazines (cbz, cbr, cb7, cbt, pdf, epub).
    /// </summary>
    public bool EnableBooks { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a frame is extracted from videos
    /// (movies, episodes) that still have no primary image.
    /// </summary>
    public bool EnableVideos { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether series, seasons, box sets and other
    /// folders inherit the image of their first child when they have none.
    /// </summary>
    public bool EnableFolderInheritance { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether an image that already exists is replaced.
    /// Off by default: anything already provided by a scraper or a local file is kept.
    /// </summary>
    public bool OverwriteExisting { get; set; }

    /// <summary>
    /// Gets or sets the 0-based page of a document to use as the cover.
    /// Page 0 is the very first page, which is the actual cover of a scan.
    /// </summary>
    public int PageIndex { get; set; }

    /// <summary>
    /// Gets or sets how far into a video, in seconds, the grabbed frame sits.
    /// 0 is the very first frame. Note that many releases open on a black frame or a
    /// distributor logo, so a few seconds in often looks better.
    /// </summary>
    public int VideoFrameSeconds { get; set; }

    /// <summary>
    /// Gets or sets the longest edge, in pixels, of a rendered document page.
    /// </summary>
    public int MaxDimension { get; set; } = 1600;

    /// <summary>
    /// Gets or sets the JPEG quality used when a page has to be re-encoded.
    /// </summary>
    public int JpegQuality { get; set; } = 90;
}

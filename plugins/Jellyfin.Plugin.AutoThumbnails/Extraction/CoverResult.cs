using MediaBrowser.Model.Drawing;

namespace Jellyfin.Plugin.AutoThumbnails.Extraction;

/// <summary>
/// An extracted cover image held in memory.
/// </summary>
/// <param name="Data">The encoded image bytes.</param>
/// <param name="Format">The image format of <paramref name="Data"/>.</param>
/// <param name="Source">A short description of where the image came from, for logging.</param>
public sealed record CoverResult(byte[] Data, ImageFormat Format, string Source);

using System;
using System.IO;
using MediaBrowser.Model.Drawing;

namespace Jellyfin.Plugin.AutoThumbnails.Extraction;

/// <summary>
/// Helpers for recognising the image files stored inside archives.
/// </summary>
public static class ImageFormats
{
    /// <summary>
    /// Returns the <see cref="ImageFormat"/> for a file name, or <c>null</c> when it is not
    /// an image format Jellyfin can store.
    /// </summary>
    /// <param name="fileName">The entry or file name to inspect.</param>
    /// <returns>The matching format, or <c>null</c>.</returns>
    public static ImageFormat? FromFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".jfif" => ImageFormat.Jpg,
            ".png" => ImageFormat.Png,
            ".webp" => ImageFormat.Webp,
            ".gif" => ImageFormat.Gif,
            ".bmp" => ImageFormat.Bmp,
            _ => null
        };
    }

    /// <summary>
    /// Gets the MIME type for an <see cref="ImageFormat"/>.
    /// </summary>
    /// <param name="format">The image format.</param>
    /// <returns>The MIME type.</returns>
    public static string ToMimeType(ImageFormat format) => format switch
    {
        ImageFormat.Png => "image/png",
        ImageFormat.Webp => "image/webp",
        ImageFormat.Gif => "image/gif",
        ImageFormat.Bmp => "image/bmp",
        ImageFormat.Svg => "image/svg+xml",
        _ => "image/jpeg"
    };

    /// <summary>
    /// Gets the canonical file extension for an <see cref="ImageFormat"/>.
    /// </summary>
    /// <param name="format">The image format.</param>
    /// <returns>The extension, including the leading dot.</returns>
    public static string ToExtension(ImageFormat format) => format switch
    {
        ImageFormat.Png => ".png",
        ImageFormat.Webp => ".webp",
        ImageFormat.Gif => ".gif",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Svg => ".svg",
        _ => ".jpg"
    };

    /// <summary>
    /// Determines whether an archive entry should be ignored: directories, macOS resource
    /// forks and hidden files that are never the actual first page.
    /// </summary>
    /// <param name="entryName">The full entry name inside the archive.</param>
    /// <returns><c>true</c> when the entry must be skipped.</returns>
    public static bool IsJunkEntry(string entryName)
    {
        if (entryName.Contains("__MACOSX", StringComparison.OrdinalIgnoreCase)
            || entryName.Contains(".DS_Store", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = Path.GetFileName(entryName);
        return name.StartsWith("._", StringComparison.Ordinal);
    }
}

using System;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Jellyfin.Plugin.ArtworkRefresher.Core;

/// <summary>
/// An image that has been decoded and checked.
/// </summary>
/// <param name="Data">The bytes to save.</param>
/// <param name="MimeType">The normalised MIME type: image/jpeg, image/png or image/webp.</param>
/// <param name="Extension">The file extension with the dot.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public sealed record ValidatedImage(byte[] Data, string MimeType, string Extension, int Width, int Height);

/// <summary>
/// Checks downloaded bytes: the real format (never the server's Content-Type), the size and
/// that the image decodes at all.
/// </summary>
public static class ImageValidator
{
    /// <summary>
    /// Validates bytes as an image.
    /// </summary>
    /// <param name="data">The bytes.</param>
    /// <param name="minWidth">The smallest width.</param>
    /// <param name="minHeight">The smallest height.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>The image, or null when refused.</returns>
    public static ValidatedImage? Validate(byte[] data, int minWidth, int minHeight, out string error)
    {
        error = string.Empty;
        if (data is null || data.Length < 64)
        {
            error = "too small to be an image";
            return null;
        }

        try
        {
            var info = Image.Identify(data);
            var format = Image.DetectFormat(data);
            string mime;
            string ext;
            if (format is JpegFormat)
            {
                (mime, ext) = ("image/jpeg", ".jpg");
            }
            else if (format is PngFormat)
            {
                (mime, ext) = ("image/png", ".png");
            }
            else if (format is WebpFormat)
            {
                (mime, ext) = ("image/webp", ".webp");
            }
            else
            {
                error = "unsupported image format " + format.Name;
                return null;
            }

            if (info.Width < minWidth || info.Height < minHeight)
            {
                error = "image is smaller than the minimum (" + info.Width + "x" + info.Height + ")";
                return null;
            }

            // Decode fully: Identify only reads the header, a truncated or corrupt file passes it.
            using var image = Image.Load<Rgba32>(data);
            return new ValidatedImage(data, mime, ext, image.Width, image.Height);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException or ImageFormatException)
        {
            error = "not a valid image";
            return null;
        }
    }
}

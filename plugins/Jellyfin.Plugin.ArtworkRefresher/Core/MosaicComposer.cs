using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Jellyfin.Plugin.ArtworkRefresher.Core;

/// <summary>
/// One image that can go in a mosaic.
/// </summary>
/// <param name="ItemId">The id of the item the image belongs to.</param>
/// <param name="ImageTag">A value that changes when the image changes (path plus modification time).</param>
/// <param name="Path">The path of the image file.</param>
public sealed record MosaicInput(Guid ItemId, string ImageTag, string Path);

/// <summary>
/// Builds mosaics from images the server already holds. Deterministic: the same inputs give the
/// same signature, and the same signature means nothing is regenerated.
/// </summary>
public static class MosaicComposer
{
    /// <summary>
    /// Computes the signature of a mosaic, over the category and the sorted inputs.
    /// </summary>
    /// <param name="categoryId">The category id.</param>
    /// <param name="inputs">All candidate inputs (not only the chosen ones).</param>
    /// <param name="settings">The rendering settings string (size, grid, quality).</param>
    /// <returns>A hex SHA-256.</returns>
    public static string Signature(Guid categoryId, IEnumerable<MosaicInput> inputs, string settings)
    {
        var sb = new StringBuilder();
        sb.Append(categoryId.ToString("N")).Append('|').Append(settings);
        foreach (var i in inputs.OrderBy(x => x.ItemId))
        {
            sb.Append('|').Append(i.ItemId.ToString("N")).Append(':').Append(i.ImageTag);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// Chooses which inputs go in the mosaic, stably.
    /// </summary>
    /// <param name="categoryId">The category id.</param>
    /// <param name="epoch">The rotation epoch, 0 when rotation is off.</param>
    /// <param name="inputs">All candidates.</param>
    /// <param name="count">How many tiles.</param>
    /// <returns>The chosen inputs.</returns>
    public static IReadOnlyList<MosaicInput> Choose(Guid categoryId, long epoch, IEnumerable<MosaicInput> inputs, int count)
    {
        return inputs
            .OrderBy(i => HashKey(categoryId, epoch, i.ItemId), StringComparer.Ordinal)
            .Take(Math.Max(1, count))
            .ToList();
    }

    /// <summary>
    /// Renders the mosaic as JPEG.
    /// </summary>
    /// <param name="files">The image files, in tile order.</param>
    /// <param name="columns">The columns.</param>
    /// <param name="rows">The rows.</param>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="quality">The JPEG quality.</param>
    /// <returns>The JPEG bytes, or null when no file could be read.</returns>
    public static byte[]? Render(IReadOnlyList<string> files, int columns, int rows, int width, int height, int quality)
    {
        columns = Math.Clamp(columns, 1, 6);
        rows = Math.Clamp(rows, 1, 6);
        width = Math.Clamp(width, 200, 4000);
        height = Math.Clamp(height, 200, 4000);

        var tiles = new List<Image<Rgba32>>();
        try
        {
            foreach (var file in files)
            {
                if (tiles.Count >= columns * rows)
                {
                    break;
                }

                try
                {
                    tiles.Add(Image.Load<Rgba32>(file));
                }
                catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or IOException or NotSupportedException)
                {
                    // Skip an unreadable file; the mosaic is built from what can be read.
                }
            }

            if (tiles.Count == 0)
            {
                return null;
            }

            using var canvas = new Image<Rgba32>(width, height, new Rgba32(18, 18, 20, 255));
            var cellW = width / columns;
            var cellH = height / rows;
            for (var i = 0; i < columns * rows; i++)
            {
                // Fewer images than cells: repeat from the start so there are no holes.
                var tile = tiles[i % tiles.Count];
                var col = i % columns;
                var row = i / columns;
                using var resized = tile.Clone(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(cellW, cellH),
                    Mode = ResizeMode.Crop,
                    Position = AnchorPositionMode.Center
                }));
                canvas.Mutate(ctx => ctx.DrawImage(resized, new Point(col * cellW, row * cellH), 1f));
            }

            using var ms = new MemoryStream();
            canvas.Save(ms, new JpegEncoder { Quality = Math.Clamp(quality, 40, 100) });
            return ms.ToArray();
        }
        finally
        {
            foreach (var t in tiles)
            {
                t.Dispose();
            }
        }
    }

    private static string HashKey(Guid category, long epoch, Guid item)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(category.ToString("N") + "|" + epoch + "|" + item.ToString("N"))));
}

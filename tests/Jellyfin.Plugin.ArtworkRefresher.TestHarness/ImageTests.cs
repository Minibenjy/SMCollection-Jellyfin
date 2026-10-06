using Jellyfin.Plugin.ArtworkRefresher.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Jellyfin.Plugin.ArtworkRefresher.TestHarness;

internal static class ImageTests
{
    public static Task RunAsync()
    {
        Validation();
        Mosaics();
        return Task.CompletedTask;
    }

    private static byte[] Make(int w, int h, Rgba32 color, bool png = false)
    {
        using var image = new Image<Rgba32>(w, h, color);
        using var ms = new MemoryStream();
        if (png)
        {
            image.Save(ms, new PngEncoder());
        }
        else
        {
            image.Save(ms, new JpegEncoder());
        }

        return ms.ToArray();
    }

    private static void Validation()
    {
        var jpeg = ImageValidator.Validate(Make(400, 600, new Rgba32(200, 10, 10, 255)), 300, 150, out var e1);
        Check.True(jpeg is not null && jpeg.MimeType == "image/jpeg" && jpeg.Extension == ".jpg", "image: jpeg accepted, mime normalised");
        var png = ImageValidator.Validate(Make(400, 600, new Rgba32(10, 200, 10, 255), png: true), 300, 150, out _);
        Check.True(png is not null && png.MimeType == "image/png" && png.Extension == ".png", "image: png accepted");

        Check.True(ImageValidator.Validate(Make(100, 100, new Rgba32(1, 2, 3, 255)), 300, 150, out var small) is null && small.Contains("smaller", StringComparison.Ordinal), "image: too small refused");

        // What a server sends with a 200 when it should not: an HTML page, whatever the Content-Type says.
        var html = System.Text.Encoding.UTF8.GetBytes("<html><body>" + new string('x', 500) + "</body></html>");
        Check.True(ImageValidator.Validate(html, 1, 1, out _) is null, "image: html refused");

        // Truncated file: the header is fine, the pixels are not.
        var whole = Make(400, 600, new Rgba32(50, 60, 70, 255), png: true);
        var truncated = whole.Take(whole.Length / 2).ToArray();
        Check.True(ImageValidator.Validate(truncated, 1, 1, out _) is null, "image: truncated png refused");
        Check.True(ImageValidator.Validate(new byte[10], 1, 1, out _) is null, "image: tiny buffer refused");
        Check.True(ImageValidator.Validate(new byte[2000], 1, 1, out _) is null, "image: zeros refused");

        // GIF is a valid image but not one this plugin saves.
        using var gif = new Image<Rgba32>(400, 400, new Rgba32(1, 1, 1, 255));
        using var ms = new MemoryStream();
        gif.SaveAsGif(ms);
        Check.True(ImageValidator.Validate(ms.ToArray(), 1, 1, out var why) is null && why.Contains("unsupported", StringComparison.Ordinal), "image: gif refused");
        _ = e1;
    }

    private static void Mosaics()
    {
        var dir = Path.Combine(Path.GetTempPath(), "arf-mosaic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var inputs = new List<MosaicInput>();
            for (var i = 0; i < 6; i++)
            {
                var path = Path.Combine(dir, i + ".jpg");
                File.WriteAllBytes(path, Make(300, 450, new Rgba32((byte)(i * 40), (byte)(255 - (i * 40)), 100, 255)));
                inputs.Add(new MosaicInput(new Guid(i + 1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]), "tag" + i, path));
            }

            var cat = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            var s1 = MosaicComposer.Signature(cat, inputs, "2,2,1000,1000,90,0");
            var s2 = MosaicComposer.Signature(cat, inputs.AsEnumerable().Reverse(), "2,2,1000,1000,90,0");
            Check.Equal(s1, s2, "mosaic: signature does not depend on input order");
            Check.True(s1 != MosaicComposer.Signature(cat, inputs, "2,2,1000,1000,80,0"), "mosaic: signature changes with the settings");
            var changed = inputs.ToList();
            changed[3] = changed[3] with { ImageTag = "newtag" };
            Check.True(s1 != MosaicComposer.Signature(cat, changed, "2,2,1000,1000,90,0"), "mosaic: signature changes when an image changes");
            Check.True(s1 != MosaicComposer.Signature(Guid.NewGuid(), inputs, "2,2,1000,1000,90,0"), "mosaic: signature depends on the category");

            var a = MosaicComposer.Choose(cat, 0, inputs, 4).Select(i => i.ItemId).ToList();
            var b = MosaicComposer.Choose(cat, 0, inputs.AsEnumerable().Reverse(), 4).Select(i => i.ItemId).ToList();
            Check.True(a.SequenceEqual(b) && a.Count == 4, "mosaic: choice is stable and independent of order");
            Check.True(!a.SequenceEqual(MosaicComposer.Choose(cat, 1, inputs, 4).Select(i => i.ItemId)), "mosaic: another rotation epoch can choose differently");
            Check.Equal(2, MosaicComposer.Choose(cat, 0, inputs.Take(2), 4).Count, "mosaic: fewer inputs than tiles");

            var jpeg = MosaicComposer.Render(inputs.Take(4).Select(i => i.Path).ToList(), 2, 2, 600, 600, 85);
            Check.True(jpeg is not null && ImageValidator.Validate(jpeg, 600, 600, out _) is { Width: 600, Height: 600 }, "mosaic: renders a valid image of the requested size");
            var two = MosaicComposer.Render(inputs.Take(2).Select(i => i.Path).ToList(), 2, 2, 400, 400, 85);
            Check.True(two is not null, "mosaic: fills every cell from fewer images");
            var withBad = MosaicComposer.Render([Path.Combine(dir, "missing.jpg"), inputs[0].Path], 1, 1, 400, 400, 85);
            Check.True(withBad is not null, "mosaic: an unreadable file is skipped");
            Check.True(MosaicComposer.Render([Path.Combine(dir, "missing.jpg")], 1, 1, 400, 400, 85) is null, "mosaic: nothing readable gives null");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

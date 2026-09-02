using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Docnet.Core;
using Docnet.Core.Models;
using MediaBrowser.Model.Drawing;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Jellyfin.Plugin.AutoThumbnails.Extraction;

/// <summary>
/// Renders a page of a PDF with PDFium and encodes it as a JPEG.
/// </summary>
public sealed class PdfCoverSource
{
    // PDFium keeps global state, so only one page may be rendered at a time.
    private static readonly SemaphoreSlim RenderLock = new(1, 1);
    private static int _resolverInstalled;

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PdfCoverSource"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public PdfCoverSource(ILogger logger)
    {
        _logger = logger;
        EnsureNativeResolver(logger);
    }

    /// <summary>
    /// Renders the requested page of a PDF.
    /// </summary>
    /// <param name="path">The PDF path.</param>
    /// <param name="pageIndex">The 0-based page to render.</param>
    /// <param name="maxDimension">The longest edge, in pixels, of the result.</param>
    /// <param name="jpegQuality">The JPEG quality.</param>
    /// <returns>The rendered cover, or <c>null</c> when the document could not be read.</returns>
    public CoverResult? Extract(string path, int pageIndex, int maxDimension, int jpegQuality)
    {
        RenderLock.Wait();
        try
        {
            using var document = DocLib.Instance.GetDocReader(path, new PageDimensions(maxDimension, maxDimension));
            var pageCount = document.GetPageCount();
            if (pageCount == 0)
            {
                return null;
            }

            var index = Math.Clamp(pageIndex, 0, pageCount - 1);
            using var page = document.GetPageReader(index);

            var width = page.GetPageWidth();
            var height = page.GetPageHeight();
            var raw = page.GetImage();

            if (width <= 0 || height <= 0 || raw.Length < width * height * 4)
            {
                _logger.LogDebug("PDFium returned an unusable page for {Path}", path);
                return null;
            }

            using var rendered = Image.LoadPixelData<Bgra32>(raw, width, height);

            // PDF pages have no background of their own; anything unpainted is transparent
            // and would end up black once flattened into a JPEG.
            using var flattened = new Image<Rgb24>(width, height, new Rgb24(255, 255, 255));
            flattened.Mutate(ctx => ctx.DrawImage(rendered, 1f));

            using var output = new MemoryStream();
            flattened.SaveAsJpeg(output, new JpegEncoder { Quality = jpegQuality });

            return new CoverResult(output.ToArray(), ImageFormat.Jpg, FormattableString.Invariant($"page {index}"));
        }
        finally
        {
            RenderLock.Release();
        }
    }

    /// <summary>
    /// Points Docnet's <c>pdfium</c> import at the copy shipped next to the plugin. Jellyfin
    /// loads plugins from a directory that is not on the default native search path, so
    /// without this the P/Invoke fails with a DllNotFoundException.
    /// </summary>
    /// <param name="logger">The logger.</param>
    private static void EnsureNativeResolver(ILogger logger)
    {
        if (Interlocked.Exchange(ref _resolverInstalled, 1) == 1)
        {
            return;
        }

        var pluginDirectory = Path.GetDirectoryName(typeof(PdfCoverSource).Assembly.Location);
        if (string.IsNullOrEmpty(pluginDirectory))
        {
            return;
        }

        var candidates = NativeCandidates(pluginDirectory);

        NativeLibrary.SetDllImportResolver(
            typeof(DocLib).Assembly,
            (name, assembly, searchPath) =>
            {
                if (!name.Contains("pdfium", StringComparison.OrdinalIgnoreCase))
                {
                    return IntPtr.Zero;
                }

                var found = candidates.FirstOrDefault(File.Exists);
                if (found is null)
                {
                    logger.LogWarning("pdfium native library not found next to the plugin; PDF covers are unavailable");
                    return IntPtr.Zero;
                }

                return NativeLibrary.Load(found);
            });
    }

    /// <summary>
    /// Builds the list of places pdfium might sit, for the platform actually running.
    /// </summary>
    /// <remarks>
    /// Jellyfin runs on Linux, Windows and macOS, on both x64 and arm64, and the
    /// Docnet package ships a native binary per platform under <c>runtimes/&lt;rid&gt;/native</c>.
    /// Probing only the Linux paths would silently disable PDF covers everywhere else —
    /// the extractor would keep working for archives and quietly return nothing for PDFs.
    /// A flat copy beside the assembly is tried first so that a hand-assembled install,
    /// or a packaging step that flattened the tree, still works.
    /// </remarks>
    /// <param name="pluginDirectory">Directory the plugin assembly was loaded from.</param>
    /// <returns>Candidate paths, most specific first.</returns>
    private static string[] NativeCandidates(string pluginDirectory)
    {
        var (fileName, altName) = OperatingSystem.IsWindows() ? ("pdfium.dll", "pdfium.dll")
            : OperatingSystem.IsMacOS() ? ("pdfium.dylib", "libpdfium.dylib")
            : ("pdfium.so", "libpdfium.so");

        var rids = OperatingSystem.IsWindows()
            ? new[] { "win-x64", "win-x86", "win-arm64" }
            : OperatingSystem.IsMacOS()
                ? new[] { "osx-arm64", "osx-x64" }
                : new[] { "linux", "linux-x64", "linux-arm64", "linux-arm", "linux-musl-x64" };

        // The running architecture first, so an x64 binary is not loaded on arm64 just
        // because it happens to be listed earlier.
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64"
        };

        var ordered = rids.OrderByDescending(r => r.EndsWith(arch, StringComparison.Ordinal)).ToArray();

        var paths = new List<string>
        {
            Path.Combine(pluginDirectory, fileName),
            Path.Combine(pluginDirectory, altName)
        };

        foreach (var rid in ordered)
        {
            paths.Add(Path.Combine(pluginDirectory, "runtimes", rid, "native", fileName));
            paths.Add(Path.Combine(pluginDirectory, "runtimes", rid, "native", altName));
        }

        return paths.Distinct(StringComparer.Ordinal).ToArray();
    }
}

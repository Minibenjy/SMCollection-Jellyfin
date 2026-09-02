using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using MediaBrowser.Model.Drawing;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace Jellyfin.Plugin.AutoThumbnails.Extraction;

/// <summary>
/// Pulls the first page out of comic and e-book archives.
/// </summary>
public sealed class ArchiveCoverSource
{
    /// <summary>
    /// Extensions handled by the built-in zip reader.
    /// </summary>
    public static readonly string[] ZipExtensions = [".cbz", ".zip", ".epub"];

    /// <summary>
    /// Extensions handled by SharpCompress (rar, 7z, tar families).
    /// </summary>
    public static readonly string[] OtherExtensions = [".cbr", ".rar", ".cb7", ".7z", ".cbt", ".tar"];

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArchiveCoverSource"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public ArchiveCoverSource(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether the extension is an archive this source can read.
    /// </summary>
    /// <param name="extension">The lower-case extension, including the dot.</param>
    /// <returns><c>true</c> when supported.</returns>
    public static bool Supports(string extension)
        => ZipExtensions.Contains(extension) || OtherExtensions.Contains(extension);

    /// <summary>
    /// Extracts the requested page from an archive.
    /// </summary>
    /// <param name="path">The archive path.</param>
    /// <param name="pageIndex">The 0-based page to use.</param>
    /// <returns>The cover, or <c>null</c> when the archive holds no usable image.</returns>
    public CoverResult? Extract(string path, int pageIndex)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return ZipExtensions.Contains(extension)
            ? FromZip(path, extension == ".epub", pageIndex)
            : FromCompressed(path, pageIndex);
    }

    private static int PickIndex(int count, int pageIndex)
        => Math.Clamp(pageIndex, 0, count - 1);

    private CoverResult? FromZip(string path, bool isEpub, int pageIndex)
    {
        using var zip = ZipFile.OpenRead(path);

        if (isEpub)
        {
            var declared = FindEpubCover(zip);
            if (declared is not null)
            {
                return declared;
            }
        }

        var entries = zip.Entries
            .Where(e => e.Length > 0
                        && !ImageFormats.IsJunkEntry(e.FullName)
                        && ImageFormats.FromFileName(e.FullName) is not null)
            .OrderBy(e => e.FullName, NaturalComparer.Instance)
            .ToList();

        if (entries.Count == 0)
        {
            return null;
        }

        var entry = entries[PickIndex(entries.Count, pageIndex)];
        using var stream = entry.Open();
        return Read(stream, entry.FullName, entry.Length);
    }

    private CoverResult? FromCompressed(string path, int pageIndex)
    {
        try
        {
            return FromRandomAccess(path, pageIndex);
        }
        catch (Exception ex)
        {
            // Solid archives cannot be read out of order, and SharpCompress rejects some
            // older RAR variants outright when it indexes them. Its sequential reader
            // copes with both, so fall back to that rather than giving up on the file.
            _logger.LogDebug(ex, "Indexed read failed for {Path}, falling back to a sequential read", path);
            return FromSequential(path, pageIndex);
        }
    }

    private static CoverResult? FromRandomAccess(string path, int pageIndex)
    {
        using var archive = ArchiveFactory.Open(path);

        var entries = archive.Entries
            .Where(e => !e.IsDirectory
                        && e.Size > 0
                        && e.Key is not null
                        && !ImageFormats.IsJunkEntry(e.Key)
                        && ImageFormats.FromFileName(e.Key) is not null)
            .OrderBy(e => e.Key, NaturalComparer.Instance)
            .ToList();

        if (entries.Count == 0)
        {
            return null;
        }

        var entry = entries[PickIndex(entries.Count, pageIndex)];
        using var stream = entry.OpenEntryStream();
        return Read(stream, entry.Key!, entry.Size);
    }

    private CoverResult? FromSequential(string path, int pageIndex)
    {
        // The sequential reader is single-pass, so walk the archive once to learn the page
        // names, then reopen it to read the one page actually wanted.
        var names = EnumerateSequentially(path).ToList();
        if (names.Count == 0)
        {
            return null;
        }

        names.Sort(NaturalComparer.Instance);
        var target = names[PickIndex(names.Count, pageIndex)];

        using var file = File.OpenRead(path);
        using var reader = ReaderFactory.Open(file);
        while (TryMoveToNextEntry(reader, path))
        {
            if (reader.Entry.IsDirectory || !string.Equals(reader.Entry.Key, target, StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = reader.OpenEntryStream();
            return Read(stream, target, reader.Entry.Size);
        }

        return null;
    }

    private IEnumerable<string> EnumerateSequentially(string path)
    {
        using var file = File.OpenRead(path);
        using var reader = ReaderFactory.Open(file);
        while (TryMoveToNextEntry(reader, path))
        {
            var key = reader.Entry.Key;
            if (reader.Entry.IsDirectory
                || key is null
                || ImageFormats.IsJunkEntry(key)
                || ImageFormats.FromFileName(key) is null)
            {
                continue;
            }

            yield return key;
        }
    }

    /// <summary>
    /// Advances the reader, treating a header it cannot parse as the end of the archive.
    /// Plenty of scene-released comic archives carry junk or a recovery record after the
    /// last real entry, and refusing the whole file over that would lose the cover.
    /// </summary>
    private bool TryMoveToNextEntry(IReader reader, string path)
    {
        try
        {
            return reader.MoveToNextEntry();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stopping at an unreadable header in {Path}", path);
            return false;
        }
    }

    private CoverResult? FindEpubCover(ZipArchive zip)
    {
        try
        {
            var container = zip.GetEntry("META-INF/container.xml");
            if (container is null)
            {
                return null;
            }

            string opfPath;
            using (var containerStream = container.Open())
            {
                opfPath = XDocument.Load(containerStream)
                    .Descendants()
                    .Where(e => e.Name.LocalName == "rootfile")
                    .Select(e => (string?)e.Attribute("full-path"))
                    .FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? string.Empty;
            }

            var opfEntry = opfPath.Length == 0 ? null : zip.GetEntry(opfPath);
            if (opfEntry is null)
            {
                return null;
            }

            XDocument opf;
            using (var opfStream = opfEntry.Open())
            {
                opf = XDocument.Load(opfStream);
            }

            var items = opf.Descendants().Where(e => e.Name.LocalName == "item").ToList();

            // The EPUB 3 way: an item flagged as the cover image.
            var href = items
                .FirstOrDefault(e => ((string?)e.Attribute("properties"))?.Contains("cover-image", StringComparison.OrdinalIgnoreCase) == true)
                ?.Attribute("href")?.Value;

            // The EPUB 2 way: a metadata entry naming the manifest id of the cover.
            if (string.IsNullOrEmpty(href))
            {
                var coverId = opf.Descendants()
                    .Where(e => e.Name.LocalName == "meta"
                                && string.Equals((string?)e.Attribute("name"), "cover", StringComparison.OrdinalIgnoreCase))
                    .Select(e => (string?)e.Attribute("content"))
                    .FirstOrDefault(id => !string.IsNullOrEmpty(id));

                if (!string.IsNullOrEmpty(coverId))
                {
                    href = items
                        .FirstOrDefault(e => string.Equals((string?)e.Attribute("id"), coverId, StringComparison.Ordinal))
                        ?.Attribute("href")?.Value;
                }
            }

            if (string.IsNullOrEmpty(href))
            {
                return null;
            }

            var baseDirectory = Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? string.Empty;
            var full = string.IsNullOrEmpty(baseDirectory) ? href : baseDirectory + "/" + href;
            var normalized = Normalize(Uri.UnescapeDataString(full));

            var coverEntry = zip.GetEntry(normalized)
                             ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, normalized, StringComparison.OrdinalIgnoreCase));

            if (coverEntry is null || ImageFormats.FromFileName(coverEntry.FullName) is null)
            {
                return null;
            }

            using var coverStream = coverEntry.Open();
            return Read(coverStream, coverEntry.FullName, coverEntry.Length);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException)
        {
            _logger.LogDebug(ex, "Could not read the EPUB manifest, falling back to the first image");
            return null;
        }
    }

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == ".." && parts.Count > 0)
            {
                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(segment);
        }

        return string.Join('/', parts);
    }

    private static CoverResult? Read(Stream stream, string entryName, long size)
    {
        var format = ImageFormats.FromFileName(entryName);
        if (format is null)
        {
            return null;
        }

        using var buffer = size > 0 && size < int.MaxValue
            ? new MemoryStream((int)size)
            : new MemoryStream();

        stream.CopyTo(buffer);
        return buffer.Length == 0 ? null : new CoverResult(buffer.ToArray(), format.Value, entryName);
    }
}

using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.AutoThumbnails.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoThumbnails.Extraction;

/// <summary>
/// Chooses the right reader for a document and returns its first page.
/// </summary>
public sealed class CoverExtractor
{
    private readonly ILogger _logger;
    private readonly ArchiveCoverSource _archives;
    private readonly PdfCoverSource _pdf;

    /// <summary>
    /// Initializes a new instance of the <see cref="CoverExtractor"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public CoverExtractor(ILogger logger)
    {
        _logger = logger;
        _archives = new ArchiveCoverSource(logger);
        _pdf = new PdfCoverSource(logger);
    }

    /// <summary>
    /// Gets a value indicating whether a file can have a cover extracted from it.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <returns><c>true</c> when the format is supported.</returns>
    public static bool IsSupportedFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension == ".pdf" || ArchiveCoverSource.Supports(extension);
    }

    /// <summary>
    /// Resolves the file to read for an item path. Books are usually a single file, but a
    /// book can also be a folder holding the actual document.
    /// </summary>
    /// <param name="path">The item path.</param>
    /// <returns>The document path, or <c>null</c> when there is nothing to read.</returns>
    public static string? ResolveDocument(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (File.Exists(path))
        {
            return IsSupportedFile(path) ? path : null;
        }

        if (!Directory.Exists(path))
        {
            return null;
        }

        return Directory.EnumerateFiles(path)
            .Where(IsSupportedFile)
            .OrderBy(Path.GetFileName, NaturalComparer.Instance)
            .FirstOrDefault();
    }

    /// <summary>
    /// Extracts a cover for an item path.
    /// </summary>
    /// <param name="path">The item path; either a document or a folder holding one.</param>
    /// <param name="configuration">The plugin configuration.</param>
    /// <returns>The cover, or <c>null</c> when none could be produced.</returns>
    public CoverResult? Extract(string? path, PluginConfiguration configuration)
    {
        var document = ResolveDocument(path);
        if (document is null)
        {
            return null;
        }

        try
        {
            var page = Math.Max(0, configuration.PageIndex);
            var result = Path.GetExtension(document).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                ? _pdf.Extract(document, page, Math.Max(200, configuration.MaxDimension), Math.Clamp(configuration.JpegQuality, 1, 100))
                : _archives.Extract(document, page);

            if (result is null)
            {
                _logger.LogDebug("No usable page found in {Path}", document);
            }

            return result;
        }
        catch (Exception ex)
        {
            // A single unreadable or corrupt file must never abort a library scan.
            _logger.LogWarning(ex, "Failed to extract a cover from {Path}", document);
            return null;
        }
    }
}

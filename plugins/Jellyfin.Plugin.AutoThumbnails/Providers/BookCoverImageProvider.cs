using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AutoThumbnails.Extraction;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoThumbnails.Providers;

/// <summary>
/// Supplies the first page of a book, comic or magazine as its primary image.
/// Runs during normal library scans, after every metadata scraper has had its turn.
/// </summary>
public class BookCoverImageProvider : IDynamicImageProvider, IHasOrder
{
    private readonly ILogger<BookCoverImageProvider> _logger;
    private readonly CoverExtractor _extractor;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookCoverImageProvider"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public BookCoverImageProvider(ILogger<BookCoverImageProvider> logger)
    {
        _logger = logger;
        _extractor = new CoverExtractor(logger);
    }

    /// <inheritdoc />
    public string Name => "Auto Thumbnails";

    /// <summary>
    /// Gets the provider order. High, so that a real cover from Comic Vine, Bookshelf or a
    /// local image file is always preferred over an extracted page.
    /// </summary>
    public int Order => 1000;

    /// <inheritdoc />
    public bool Supports(BaseItem item)
        => Plugin.Config.EnableBooks
           && item is Book
           && CoverExtractor.ResolveDocument(item.Path) is not null;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => [ImageType.Primary];

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetImage(BaseItem item, ImageType type, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Config;

        if (type != ImageType.Primary
            || (!configuration.OverwriteExisting && item.HasImage(ImageType.Primary, 0)))
        {
            return Task.FromResult(new DynamicImageResponse { HasImage = false });
        }

        cancellationToken.ThrowIfCancellationRequested();

        var cover = _extractor.Extract(item.Path, configuration);
        if (cover is null)
        {
            return Task.FromResult(new DynamicImageResponse { HasImage = false });
        }

        _logger.LogDebug("Extracted {Source} of {Name} as its thumbnail", cover.Source, item.Name);

        return Task.FromResult(new DynamicImageResponse
        {
            Stream = new MemoryStream(cover.Data, false),
            Format = cover.Format,
            HasImage = true
        });
    }
}

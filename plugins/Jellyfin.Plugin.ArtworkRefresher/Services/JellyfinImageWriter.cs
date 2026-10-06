using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArtworkRefresher.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArtworkRefresher.Services;

/// <summary>
/// The only place images are written to an item. Goes through Jellyfin's own pipeline and
/// always keeps the files in the server's metadata folder, never next to the media: a sidecar
/// next to a PDF or a comic is exactly what once covered the real cover.
/// </summary>
public sealed class JellyfinImageWriter
{
    private readonly IProviderManager _providerManager;
    private readonly string _tempDirectory;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinImageWriter"/> class.
    /// </summary>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="tempDirectory">A directory for the temporary file handed to the server.</param>
    /// <param name="logger">The logger.</param>
    public JellyfinImageWriter(IProviderManager providerManager, string tempDirectory, ILogger logger)
    {
        _providerManager = providerManager;
        _tempDirectory = tempDirectory;
        _logger = logger;
    }

    /// <summary>
    /// Saves a validated image into a slot of an item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="slot">The slot.</param>
    /// <param name="image">The validated image.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SaveAsync(BaseItem item, ImageSlot slot, ValidatedImage image, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_tempDirectory);
        var temp = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N") + image.Extension);
        try
        {
            await File.WriteAllBytesAsync(temp, image.Data, cancellationToken).ConfigureAwait(false);

            // Only the types that can repeat take an explicit index; null lets the server pick the single slot.
            int? index = slot.Type is ImageType.Backdrop or ImageType.Screenshot ? slot.Index : null;

            await _providerManager.SaveImage(item, temp, image.MimeType, slot.Type, index, false, cancellationToken).ConfigureAwait(false);
            await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Artwork Refresher: could not delete the temporary file {File}", temp);
            }
        }
    }
}

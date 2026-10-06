using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtworkRefresher.Core;
using Jellyfin.Plugin.ArtworkRefresher.Services;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArtworkRefresher.Injection;

/// <summary>
/// Notices items added to the library (ILibraryManager.ItemAdded) and fills their artwork, and optionally
/// their empty metadata, a little later so Jellyfin's own refresh has finished. Honours the time window
/// unless the administrator turned that off. Does nothing when the plugin is disabled.
/// </summary>
public sealed class NewItemsHostedService : IHostedService, IDisposable
{
    private static readonly BaseItemKind[] Kinds =
    [
        BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode,
        BaseItemKind.MusicAlbum, BaseItemKind.MusicArtist, BaseItemKind.BoxSet
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly ArtworkRefreshService _service;
    private readonly ILogger<NewItemsHostedService> _logger;
    private readonly NewItemQueue _queue = new();
    private Timer? _timer;
    private int _busy;

    /// <summary>
    /// Initializes a new instance of the <see cref="NewItemsHostedService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="service">The refresh service.</param>
    /// <param name="logger">The logger.</param>
    public NewItemsHostedService(ILibraryManager libraryManager, ArtworkRefreshService service, ILogger<NewItemsHostedService> logger)
    {
        _libraryManager = libraryManager;
        _service = service;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _timer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _timer?.Dispose();

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            var configuration = Plugin.Config;
            var item = e.Item;
            if (!configuration.Enabled || !configuration.FillNewItemsOnAdd || item is null || item.IsVirtualItem || !Kinds.Contains(item.GetBaseItemKind()))
            {
                return;
            }

            _queue.Add(item.Id, DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Artwork Refresher: could not queue a new item");
        }
    }

    private async Task TickAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            var configuration = Plugin.Config;
            if (!configuration.Enabled || !configuration.FillNewItemsOnAdd || _queue.Count == 0)
            {
                return;
            }

            var due = _queue.Due(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(Math.Clamp(configuration.NewItemDelaySeconds, 0, 3600)), 50);
            if (due.Count == 0)
            {
                return;
            }

            if (configuration.NewItemsRespectWindow && configuration.RefreshWindowEnabled
                && RefreshWindowPolicy.TryParseTime(configuration.RefreshWindowStart, out var start)
                && RefreshWindowPolicy.TryParseTime(configuration.RefreshWindowEnd, out var end)
                && !RefreshWindowPolicy.IsInside(DateTimeOffset.UtcNow, RefreshWindowPolicy.ResolveZone(configuration.RefreshWindowTimeZone), start, end))
            {
                // Outside the window: they stay queued and are filled when it opens.
                return;
            }

            if (await _service.ProcessNewItemsAsync(due, CancellationToken.None).ConfigureAwait(false))
            {
                _queue.Remove(due);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Artwork Refresher: filling new items failed");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }
}

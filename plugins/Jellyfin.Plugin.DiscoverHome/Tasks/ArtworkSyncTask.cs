using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DiscoverHome.Artwork;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscoverHome.Tasks;

/// <summary>
/// Refreshes the cached studio logos once a day.
/// </summary>
/// <remarks>
/// Runs on the server rather than in the browser so one download serves every client,
/// and so a new studio that appears overnight has artwork ready before anyone opens
/// the home screen.
/// </remarks>
public class ArtworkSyncTask : IScheduledTask
{
    private readonly StudioArtworkCache _cache;
    private readonly ILogger<ArtworkSyncTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtworkSyncTask"/> class.
    /// </summary>
    /// <param name="cache">Studio artwork cache.</param>
    /// <param name="logger">Logger.</param>
    public ArtworkSyncTask(StudioArtworkCache cache, ILogger<ArtworkSyncTask> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync Discover Home artwork";

    /// <inheritdoc />
    public string Key => "DiscoverHomeArtworkSync";

    /// <inheritdoc />
    public string Description =>
        "Downloads logos for the studios in this library so the Discover Home carousels can show them.";

    /// <inheritdoc />
    public string Category => "Discover Home";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var total = await _cache.SyncAsync(progress, cancellationToken).ConfigureAwait(false);

        progress.Report(100);
        _logger.LogInformation("DiscoverHome: artwork sync complete ({Total} logos cached).", total);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Early morning: the upstream repository is a public GitHub raw endpoint, and
        // there is no reason to compete with peak viewing for bandwidth.
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        };
    }
}

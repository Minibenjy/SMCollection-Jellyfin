using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArtworkRefresher.Core;
using Jellyfin.Plugin.ArtworkRefresher.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ArtworkRefresher.Tasks;

/// <summary>
/// The scheduled sweep. Jellyfin wakes it up every half hour; it only works when the persisted
/// next-eligible time has passed and the clock is inside the configured window. That survives
/// restarts, daylight saving changes and windows that cross midnight.
/// </summary>
public class ArtworkRefreshTask : IScheduledTask
{
    private readonly ArtworkRefreshService _service;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtworkRefreshTask"/> class.
    /// </summary>
    /// <param name="service">The refresh service.</param>
    public ArtworkRefreshTask(ArtworkRefreshService service)
    {
        _service = service;
    }

    /// <inheritdoc />
    public string Name => "Refresh artwork";

    /// <inheritdoc />
    public string Key => "ArtworkRefresherRefresh";

    /// <inheritdoc />
    public string Description => "Looks for new posters, backdrops, logos and category images from the sources enabled in the Artwork Refresher settings. Runs only inside the configured time window and no more often than the configured interval. Locked items, excluded libraries and book covers are never touched.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(30).Ticks
        }
    ];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Config;
        if (!configuration.Enabled)
        {
            progress.Report(100);
            return;
        }

        var zone = RefreshWindowPolicy.ResolveZone(configuration.RefreshWindowTimeZone);
        var okStart = RefreshWindowPolicy.TryParseTime(configuration.RefreshWindowStart, out var start);
        var okEnd = RefreshWindowPolicy.TryParseTime(configuration.RefreshWindowEnd, out var end);
        var next = _service.State.Use(d => d.Scheduler.NextEligibleRunUtc);

        if (!RefreshWindowPolicy.IsDue(DateTimeOffset.UtcNow, next, configuration.RefreshWindowEnabled && okStart && okEnd, zone, start, end))
        {
            progress.Report(100);
            return;
        }

        await _service.RunAsync(new RunRequest { Scheduled = true }, progress, cancellationToken).ConfigureAwait(false);
    }
}

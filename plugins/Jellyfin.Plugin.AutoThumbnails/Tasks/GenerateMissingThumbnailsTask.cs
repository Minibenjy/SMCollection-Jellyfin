using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AutoThumbnails.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AutoThumbnails.Tasks;

/// <summary>
/// The scheduled sweep. Shares its implementation, progress and log with the plugin page,
/// so a run started from either place shows up in both.
/// </summary>
public class GenerateMissingThumbnailsTask : IScheduledTask
{
    private readonly ThumbnailJobService _jobs;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenerateMissingThumbnailsTask"/> class.
    /// </summary>
    /// <param name="jobs">The job service.</param>
    public GenerateMissingThumbnailsTask(ThumbnailJobService jobs)
    {
        _jobs = jobs;
    }

    /// <inheritdoc />
    public string Name => "Generate missing thumbnails";

    /// <inheritdoc />
    public string Key => "AutoThumbnailsGenerateMissing";

    /// <inheritdoc />
    public string Description => "Gives every item without a thumbnail one, taken from its own content: the first page of a comic, book or PDF, an early frame of a video, or the first child of a series, season or collection. Existing artwork is never touched.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks
        }
    ];

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Config;

        return _jobs.RunAsync(
            new JobRequest
            {
                Books = configuration.EnableBooks,
                Videos = configuration.EnableVideos,
                Folders = configuration.EnableFolderInheritance,
                Regenerate = configuration.OverwriteExisting
            },
            progress,
            cancellationToken);
    }
}

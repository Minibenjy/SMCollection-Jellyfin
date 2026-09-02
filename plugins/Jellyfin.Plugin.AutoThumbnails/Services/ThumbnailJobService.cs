using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AutoThumbnails.Configuration;
using Jellyfin.Plugin.AutoThumbnails.Extraction;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoThumbnails.Services;

/// <summary>
/// Runs thumbnail generation and keeps the progress and log that the dashboard shows.
/// A single run at a time, shared by the scheduled task and the plugin page.
/// </summary>
public sealed class ThumbnailJobService
{
    private const int MaxLogEntries = 500;

    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<ThumbnailJobService> _logger;
    private readonly CoverExtractor _extractor;

    private readonly Lock _sync = new();
    private readonly List<JobLogEntry> _log = new();

    // Only one run at a time, whichever entry point asked for it.
    private readonly SemaphoreSlim _runGate = new(1, 1);

    private long _sequence;
    private CancellationTokenSource? _cancellation;
    private JobStatus _status = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ThumbnailJobService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="mediaEncoder">The media encoder.</param>
    /// <param name="logger">The logger.</param>
    public ThumbnailJobService(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IMediaEncoder mediaEncoder,
        ILogger<ThumbnailJobService> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
        _extractor = new CoverExtractor(logger);
    }

    /// <summary>
    /// Gets a value indicating whether a run is in progress.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _status.State is "running" or "cancelling";
            }
        }
    }

    /// <summary>
    /// Lists the libraries that can be targeted.
    /// </summary>
    /// <returns>The libraries.</returns>
    public IReadOnlyList<LibraryInfo> GetLibraries()
        => _libraryManager.GetVirtualFolders()
            .Where(f => !string.IsNullOrEmpty(f.ItemId))
            .Select(f => new LibraryInfo
            {
                Id = f.ItemId,
                Name = f.Name,
                CollectionType = f.CollectionType?.ToString() ?? string.Empty
            })
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// Gets a snapshot of the run, including log lines newer than <paramref name="sinceSequence"/>.
    /// </summary>
    /// <param name="sinceSequence">The highest sequence the caller already has.</param>
    /// <returns>The status.</returns>
    public JobStatus GetStatus(long sinceSequence)
    {
        lock (_sync)
        {
            return new JobStatus
            {
                State = _status.State,
                Phase = _status.Phase,
                Processed = _status.Processed,
                Total = _status.Total,
                Created = _status.Created,
                Skipped = _status.Skipped,
                Failed = _status.Failed,
                Percent = _status.Percent,
                StartedAt = _status.StartedAt,
                FinishedAt = _status.FinishedAt,
                LatestSequence = _sequence,
                Log = _log.Where(e => e.Sequence > sinceSequence).ToList()
            };
        }
    }

    /// <summary>
    /// Starts a run in the background.
    /// </summary>
    /// <param name="request">What to cover.</param>
    /// <returns><c>true</c> when the run started, <c>false</c> when one is already going.</returns>
    public bool TryStart(JobRequest request)
    {
        CancellationToken token;

        lock (_sync)
        {
            if (_status.State is "running" or "cancelling")
            {
                return false;
            }

            _status = new JobStatus { State = "running", StartedAt = DateTime.UtcNow };
        }

        token = CancellationToken.None;

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RunAsync(request, null, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // RunAsync has already recorded the outcome; nothing may escape here.
                    _logger.LogDebug(ex, "The thumbnail run ended early");
                }
            },
            CancellationToken.None);

        return true;
    }

    /// <summary>
    /// Asks the current run to stop.
    /// </summary>
    public void Cancel()
    {
        lock (_sync)
        {
            if (_status.State != "running")
            {
                return;
            }

            _status.State = "cancelling";
        }

        Append("info", "Cancelling…");
        _cancellation?.Cancel();
    }

    /// <summary>
    /// Does the actual work. Awaited directly by the scheduled task.
    /// </summary>
    /// <param name="request">What to cover.</param>
    /// <param name="progress">Optional progress sink for the scheduled task UI.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the run.</returns>
    public async Task RunAsync(JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        // The dashboard's Cancel button has to reach a run the scheduled task started too.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetCancellationSource(linked);

        try
        {
            BeginRun();
            await RunCoreAsync(request, progress, linked.Token).ConfigureAwait(false);
            SetFinalState("completed");
        }
        catch (OperationCanceledException)
        {
            SetFinalState("cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The thumbnail run failed");
            Append("error", "The run failed: " + ex.Message);
            SetFinalState("failed");
            throw;
        }
        finally
        {
            SetCancellationSource(null);
            _runGate.Release();
        }
    }

    private async Task RunCoreAsync(JobRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Config;

        // Folders come last so they can inherit an image the earlier passes just created.
        var passes = new List<(string Label, BaseItemKind[] Kinds)>();
        if (request.Books)
        {
            passes.Add(("Books and comics", [BaseItemKind.Book]));
        }

        if (request.Videos)
        {
            passes.Add(("Videos", [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video, BaseItemKind.MusicVideo]));
        }

        if (request.Folders)
        {
            passes.Add(("Series and collections", [BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.BoxSet]));
        }

        SetPhase("Looking for items…");

        var work = passes
            .Select(p => (p.Label, Items: GetCandidates(p.Kinds, request)))
            .Where(p => p.Items.Count > 0)
            .ToList();

        var total = work.Sum(w => w.Items.Count);
        SetTotal(total);

        if (total == 0)
        {
            Append("info", request.Regenerate
                ? "Nothing matches that selection."
                : "Everything already has a thumbnail; nothing to do.");
            SetPhase(string.Empty);
            progress?.Report(100);
            return;
        }

        Append("info", string.Format(CultureInfo.CurrentCulture, "{0} item(s) to process.", total));

        var processed = 0;

        foreach (var (label, items) in work)
        {
            SetPhase(label);
            Append("info", string.Format(CultureInfo.CurrentCulture, "── {0}: {1} item(s)", label, items.Count));

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var source = await ProcessAsync(item, configuration, cancellationToken).ConfigureAwait(false);
                    if (source is null)
                    {
                        Bump(skipped: 1);
                        Append("skip", string.Format(CultureInfo.CurrentCulture, "No usable page: {0}", Describe(item)));
                    }
                    else
                    {
                        Bump(created: 1);
                        Append("ok", string.Format(CultureInfo.CurrentCulture, "{0} — {1}", Describe(item), source));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Bump(failed: 1);
                    _logger.LogWarning(ex, "Could not generate a thumbnail for {Name} ({Path})", item.Name, item.Path);
                    Append("error", string.Format(CultureInfo.CurrentCulture, "{0} — {1}", Describe(item), ex.Message));
                }

                processed++;
                SetProgress(processed, total);
                progress?.Report(processed * 100.0 / total);
            }
        }

        var final = GetStatus(long.MaxValue);
        Append("info", string.Format(
            CultureInfo.CurrentCulture,
            "Done: {0} created, {1} with no usable page, {2} failed.",
            final.Created,
            final.Skipped,
            final.Failed));

        SetPhase(string.Empty);
        progress?.Report(100);
    }

    private static string Describe(BaseItem item)
    {
        var name = string.IsNullOrEmpty(item.Name) ? Path.GetFileName(item.Path) ?? "?" : item.Name;
        return item is Episode episode && episode.Series is not null
            ? episode.Series.Name + " · " + name
            : name;
    }

    private IReadOnlyList<BaseItem> GetCandidates(BaseItemKind[] kinds, JobRequest request)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = kinds,
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(true)
        };

        var libraryIds = request.LibraryIds
            .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
            .Where(guid => guid != Guid.Empty)
            .ToArray();

        if (libraryIds.Length > 0)
        {
            query.AncestorIds = libraryIds;
        }

        return _libraryManager.GetItemList(query)
            .Where(item => request.Regenerate || !item.HasImage(ImageType.Primary, 0))
            .ToList();
    }

    private async Task<string?> ProcessAsync(BaseItem item, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        if (item is Book)
        {
            var cover = _extractor.Extract(item.Path, configuration);
            if (cover is null)
            {
                return null;
            }

            using var stream = new MemoryStream(cover.Data, false);
            await SaveAsync(item, stream, ImageFormats.ToMimeType(cover.Format), cancellationToken).ConfigureAwait(false);
            return cover.Source;
        }

        if (item is Video video)
        {
            return await ExtractVideoFrameAsync(video, configuration, cancellationToken).ConfigureAwait(false);
        }

        if (item is Folder folder)
        {
            return await InheritFromChildAsync(folder, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<string?> ExtractVideoFrameAsync(Video video, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        if (video.IsPlaceHolder || string.IsNullOrEmpty(video.Path))
        {
            return null;
        }

        var videoStream = video.GetDefaultVideoStream()
                          ?? video.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);

        if (videoStream is null)
        {
            return null;
        }

        var offset = TimeSpan.FromSeconds(Math.Max(0, configuration.VideoFrameSeconds));

        var mediaSource = new MediaSourceInfo
        {
            VideoType = video.VideoType,
            IsoType = video.IsoType,
            Protocol = video.PathProtocol ?? MediaProtocol.File
        };

        var tempFile = await _mediaEncoder.ExtractVideoImage(
            video.Path,
            video.Container,
            mediaSource,
            videoStream,
            video.Video3DFormat,
            offset,
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(tempFile) || !File.Exists(tempFile))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(tempFile);
            await SaveAsync(video, stream, ImageFormats.ToMimeType(ImageFormat.Jpg), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tempFile);
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            "fotograma en {0}s",
            offset.TotalSeconds.ToString("0.#", CultureInfo.CurrentCulture));
    }

    private async Task<string?> InheritFromChildAsync(Folder folder, CancellationToken cancellationToken)
    {
        var source = folder.GetRecursiveChildren(child => child.HasImage(ImageType.Primary, 0))
            .OrderBy(child => child.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(child => child.IndexNumber ?? int.MaxValue)
            .ThenBy(child => child.SortName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        var imagePath = source?.GetImageInfo(ImageType.Primary, 0)?.Path;
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        var format = ImageFormats.FromFileName(imagePath) ?? ImageFormat.Jpg;

        await using var stream = File.OpenRead(imagePath);
        await SaveAsync(folder, stream, ImageFormats.ToMimeType(format), cancellationToken).ConfigureAwait(false);

        return "heredada de " + source!.Name;
    }

    private async Task SaveAsync(BaseItem item, Stream stream, string mimeType, CancellationToken cancellationToken)
    {
        await _providerManager.SaveImage(item, stream, mimeType, ImageType.Primary, null, cancellationToken).ConfigureAwait(false);
        await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Could not delete the temporary frame {Path}", path);
        }
    }

    private void Append(string level, string message)
    {
        lock (_sync)
        {
            _log.Add(new JobLogEntry
            {
                Sequence = ++_sequence,
                Timestamp = DateTime.UtcNow,
                Level = level,
                Message = message
            });

            if (_log.Count > MaxLogEntries)
            {
                _log.RemoveRange(0, _log.Count - MaxLogEntries);
            }
        }
    }

    /// <summary>
    /// Clears the counters and the log so every run starts from zero, whichever entry
    /// point began it.
    /// </summary>
    private void BeginRun()
    {
        lock (_sync)
        {
            _log.Clear();
            _status = new JobStatus { State = "running", StartedAt = DateTime.UtcNow };
        }
    }

    private void SetCancellationSource(CancellationTokenSource? source)
    {
        lock (_sync)
        {
            _cancellation = source;
        }
    }

    private void SetPhase(string phase)
    {
        lock (_sync)
        {
            _status.Phase = phase;
        }
    }

    private void SetTotal(int total)
    {
        lock (_sync)
        {
            _status.Total = total;
        }
    }

    private void SetProgress(int processed, int total)
    {
        lock (_sync)
        {
            _status.Processed = processed;
            _status.Percent = total == 0 ? 100 : processed * 100.0 / total;
        }
    }

    private void Bump(int created = 0, int skipped = 0, int failed = 0)
    {
        lock (_sync)
        {
            _status.Created += created;
            _status.Skipped += skipped;
            _status.Failed += failed;
        }
    }

    private void SetFinalState(string state)
    {
        lock (_sync)
        {
            _status.State = state;
            _status.FinishedAt = DateTime.UtcNow;
            if (state == "completed")
            {
                _status.Percent = 100;
            }
        }
    }
}

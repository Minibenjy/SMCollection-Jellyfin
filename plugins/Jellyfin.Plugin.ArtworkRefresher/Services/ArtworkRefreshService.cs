using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;
using Jellyfin.Plugin.ArtworkRefresher.Core;
using Jellyfin.Plugin.ArtworkRefresher.Sources;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArtworkRefresher.Services;

/// <summary>
/// Runs artwork refreshes: the scheduled sweep, the manual run and the single-item refresh all go
/// through the same pipeline (candidates, lock policy, download, validation, save).
/// </summary>
public sealed class ArtworkRefreshService : IDisposable
{
    private const int MaxLogEntries = 500;
    private const string AutoThumbnailsMarker = "AutoThumbnails";
    private const int MaxAttemptsPerSlot = 6;

    private static readonly IArtworkSource[] Sources =
    [
        new TmdbSource(), new FanartSource(), new WikimediaSource(), new OpenLibrarySource(),
        new CoverArtArchiveSource(), new GoogleCseSource()
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly IServerConfigurationManager _serverConfiguration;
    private readonly IServerApplicationPaths _paths;
    private readonly ILogger<ArtworkRefreshService> _logger;
    private readonly IProviderManager _providerManager;
    private readonly SourceRateLimiter _limiter = new();
    private readonly Lock _sync = new();
    private readonly List<RunLogEntry> _log = [];
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _itemLocks = new();

    private ArtworkStateStore? _state;
    private ArtworkHttpClient? _http;
    private JellyfinImageWriter? _writer;
    private RunStatus _status = new();
    private RunSummary _summary = new();
    private CancellationTokenSource? _cancellation;
    private long _sequence;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtworkRefreshService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="providerManager">The provider manager.</param>
    /// <param name="serverConfiguration">The server configuration.</param>
    /// <param name="paths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public ArtworkRefreshService(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IServerConfigurationManager serverConfiguration,
        IServerApplicationPaths paths,
        ILogger<ArtworkRefreshService> logger)
    {
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _serverConfiguration = serverConfiguration;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>Gets the plugin data folder.</summary>
    public string DataFolder => Plugin.Instance?.DataFolderPath ?? Path.Combine(_paths.PluginConfigurationsPath, "ArtworkRefresher");

    /// <summary>Gets the state store.</summary>
    public ArtworkStateStore State => _state ??= new ArtworkStateStore(Path.Combine(DataFolder, "state.json"));

    /// <summary>Gets a value indicating whether a run is in progress.</summary>
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

    private ArtworkHttpClient Http => _http ??= new ArtworkHttpClient(() => Plugin.Config, _limiter, _logger);

    private JellyfinImageWriter Writer => _writer ??= new JellyfinImageWriter(_providerManager, Path.Combine(DataFolder, "tmp"), _logger);

    /// <summary>
    /// Lists the libraries that can be included, excluded or locked.
    /// </summary>
    /// <returns>Id, name and collection type of every library.</returns>
    public IReadOnlyList<LibraryInfo> GetLibraries()
        => _libraryManager.GetVirtualFolders()
            .Where(f => !string.IsNullOrEmpty(f.ItemId))
            .Select(f => new LibraryInfo { Id = f.ItemId, Name = f.Name, CollectionType = f.CollectionType?.ToString() ?? string.Empty })
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// Gets a snapshot of the current or last run, with log lines newer than a sequence.
    /// </summary>
    /// <param name="sinceSequence">The highest sequence the caller already has.</param>
    /// <returns>The status.</returns>
    public RunStatus GetStatus(long sinceSequence)
    {
        lock (_sync)
        {
            var http = _http;
            return new RunStatus
            {
                State = _status.State,
                Phase = _status.Phase,
                Processed = _status.Processed,
                Total = _status.Total,
                Percent = _status.Percent,
                DryRun = _status.DryRun,
                StartedAt = _status.StartedAt,
                FinishedAt = _status.FinishedAt,
                Summary = _summary,
                LatestSequence = _sequence,
                Sources = http is null ? [] : http.Counters.ToDictionary(
                    kv => kv.Key,
                    kv => new SourceCountersDto { Queries = kv.Value.Queries, Downloads = kv.Value.Downloads, TooManyRequests = kv.Value.TooManyRequests, Failures = kv.Value.Failures }),
                Log = _log.Where(e => e.Sequence > sinceSequence).ToList()
            };
        }
    }

    /// <summary>
    /// Starts a run in the background.
    /// </summary>
    /// <param name="request">What to do.</param>
    /// <returns>True when started, false when a run is already going.</returns>
    public bool TryStart(RunRequest request)
    {
        lock (_sync)
        {
            if (_status.State is "running" or "cancelling")
            {
                return false;
            }

            _status = new RunStatus { State = "running", StartedAt = DateTime.UtcNow };
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RunAsync(request, null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "The artwork run ended early");
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
    /// Runs a refresh to the end. Awaited directly by the scheduled task.
    /// </summary>
    /// <param name="request">What to do.</param>
    /// <param name="progress">Optional progress sink for the scheduled task UI.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(RunRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var configuration = Plugin.Config;
        var started = DateTimeOffset.UtcNow;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            lock (_sync)
            {
                _cancellation = linked;
                _log.Clear();
                _summary = new RunSummary();
                _status = new RunStatus { State = "running", StartedAt = DateTime.UtcNow, DryRun = request.DryRun ?? configuration.DryRun };
            }

            Http.ResetCounters();
            State.Update(d => d.Scheduler.LastRunStartedUtc = started);

            // Window end and the longest run both stop the run; whatever was done stays done.
            DateTimeOffset? deadline = null;
            if (request.Scheduled)
            {
                var zone = RefreshWindowPolicy.ResolveZone(configuration.RefreshWindowTimeZone);
                var okStart = RefreshWindowPolicy.TryParseTime(configuration.RefreshWindowStart, out var ws);
                var okEnd = RefreshWindowPolicy.TryParseTime(configuration.RefreshWindowEnd, out var we);
                deadline = RefreshWindowPolicy.Deadline(started, configuration.RefreshWindowEnabled && okStart && okEnd, zone, ws, we, configuration.MaxRunMinutes);
            }

            var partial = await RunCoreAsync(request, configuration, deadline, progress, linked.Token).ConfigureAwait(false);
            SetFinalState(partial ? "completed (partial)" : "completed");

            var completed = DateTimeOffset.UtcNow;
            State.Update(d =>
            {
                d.Scheduler.LastRunCompletedUtc = completed;
                d.Scheduler.LastRunStatus = partial ? "partial" : "completed";
                d.Scheduler.NextEligibleRunUtc = partial ? completed : completed.AddHours(Math.Max(1, configuration.RefreshIntervalHours));
            });
        }
        catch (OperationCanceledException)
        {
            SetFinalState("cancelled");
            State.Update(d => d.Scheduler.LastRunStatus = "cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The artwork run failed");
            Append("error", "The run failed: " + ex.Message);
            SetFinalState("failed");
            State.Update(d => d.Scheduler.LastRunStatus = "failed");
            throw;
        }
        finally
        {
            try
            {
                await State.SaveAsync().ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Artwork Refresher: could not write the state file");
            }

            lock (_sync)
            {
                _cancellation = null;
            }

            _runGate.Release();
        }
    }

    /// <summary>
    /// Refreshes one item. Ignores the time window and the retry delay.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="request">What to do.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What happened to each slot.</returns>
    public async Task<ItemRefreshResult> RefreshItemAsync(BaseItem item, ItemRefreshRequest request, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Config;
        var context = new RunContext(configuration, LibraryIdSet(), ResolveLanguages(configuration));
        var options = new ProcessOptions
        {
            Mode = request.Mode ?? ArtworkRefreshMode.Replace,
            Force = request.Force,
            DryRun = request.DryRun || configuration.DryRun,
            Scheduled = false,
            OverrideExcludedLibraries = request.OverrideExcludedLibraries,
            Wanted = request.ImageTypes.Length > 0 ? request.ImageTypes : null
        };

        var result = await ProcessItemAsync(item, options, context, cancellationToken).ConfigureAwait(false);
        foreach (var s in result.Slots)
        {
            Append(s.Status is "updated" or "dryrun" ? "ok" : "skip", item.Name + " — " + s.Slot + ": " + s.Status + (s.Source is null ? string.Empty : " (" + s.Source + ")"));
        }

        await State.SaveAsync().ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _http?.Dispose();
        _runGate.Dispose();
    }

    private static IReadOnlyList<ImageType> SlotTypesFor(BaseItemKind kind) => kind switch
    {
        BaseItemKind.Movie or BaseItemKind.Series => [ImageType.Primary, ImageType.Backdrop, ImageType.Logo, ImageType.Thumb],
        BaseItemKind.Season => [ImageType.Primary, ImageType.Thumb],
        BaseItemKind.MusicArtist => [ImageType.Primary, ImageType.Backdrop, ImageType.Logo],
        BaseItemKind.BoxSet => [ImageType.Primary, ImageType.Backdrop],
        BaseItemKind.Episode or BaseItemKind.MusicAlbum or BaseItemKind.Person or BaseItemKind.Genre
            or BaseItemKind.MusicGenre or BaseItemKind.Studio or BaseItemKind.Book => [ImageType.Primary],
        _ => []
    };

    private static bool IsCategory(BaseItemKind kind)
        => kind is BaseItemKind.Genre or BaseItemKind.MusicGenre or BaseItemKind.Studio or BaseItemKind.Person or BaseItemKind.BoxSet;

    private static ImageSlot SlotFor(ImageType type) => new(type, 0);

    private static BaseItemKind KindOf(BaseItem item) => item.GetBaseItemKind();

    private async Task<bool> RunCoreAsync(RunRequest request, PluginConfiguration configuration, DateTimeOffset? deadline, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var libraryIds = LibraryIdSet();
        var context = new RunContext(configuration, libraryIds, ResolveLanguages(configuration));
        var options = new ProcessOptions
        {
            Mode = request.Mode ?? configuration.RefreshMode,
            Force = false,
            DryRun = request.DryRun ?? configuration.DryRun,
            Scheduled = request.Scheduled
        };

        SetPhase("Looking for items…");
        var passes = new List<(string Label, List<BaseItem> Items)>();
        void AddMedia(string label, params BaseItemKind[] kinds)
        {
            var items = GetMedia(kinds, request.LibraryIds.Length > 0 ? request.LibraryIds : configuration.IncludedLibraryIds);
            if (items.Count > 0)
            {
                passes.Add((label, items));
            }
        }

        AddMedia("Movies and shows", BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode);
        AddMedia("Music", BaseItemKind.MusicAlbum, BaseItemKind.MusicArtist);
        AddMedia("Books", BaseItemKind.Book);
        AddMedia("Collections", BaseItemKind.BoxSet);

        var categories = new List<BaseItem>();
        var query = new InternalItemsQuery { DtoOptions = new DtoOptions(true) };
        categories.AddRange(_libraryManager.GetGenres(query).Items.Select(t => t.Item1));
        categories.AddRange(_libraryManager.GetMusicGenres(query).Items.Select(t => t.Item1));
        categories.AddRange(_libraryManager.GetStudios(query).Items.Select(t => t.Item1));
        if (categories.Count > 0)
        {
            passes.Add(("Genres and studios", categories));
        }

        if (configuration.IncludePeople)
        {
            var people = _libraryManager.GetPeopleItems(new InternalPeopleQuery()).Cast<BaseItem>().ToList();
            if (people.Count > 0)
            {
                passes.Add(("People", people));
            }
        }

        var total = passes.Sum(p => p.Items.Count);
        SetTotal(total);
        Append("info", string.Format(CultureInfo.CurrentCulture, "{0} item(s) to look at{1}.", total, options.DryRun ? " (dry run: nothing is saved)" : string.Empty));

        var processed = 0;
        foreach (var (label, items) in passes)
        {
            SetPhase(label);
            Append("info", string.Format(CultureInfo.CurrentCulture, "── {0}: {1} item(s)", label, items.Count));
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (deadline is { } d && DateTimeOffset.UtcNow >= d)
                {
                    Append("info", "Stopped: the time window or the longest run ended. The rest continues in the next run.");
                    return true;
                }

                try
                {
                    var result = await ProcessItemAsync(item, options, context, cancellationToken).ConfigureAwait(false);
                    foreach (var s in result.Slots.Where(s => s.Status is "updated" or "dryrun"))
                    {
                        Append("ok", item.Name + " — " + s.Slot + " (" + s.Source + ")");
                    }

                    foreach (var s in result.Slots.Where(s => s.Status is "failed" or "ratelimited"))
                    {
                        Append("error", item.Name + " — " + s.Slot + ": " + s.Detail);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Bump(s => s.Failed++);
                    _logger.LogWarning(ex, "Artwork Refresher: failed on {Name}", item.Name);
                    Append("error", item.Name + " — " + ex.Message);
                }

                processed++;
                SetProgress(processed, total);
                progress?.Report(total == 0 ? 100 : processed * 100.0 / total);
                if (processed % 25 == 0)
                {
                    await State.SaveAsync().ConfigureAwait(false);
                }
            }
        }

        var sum = _summary;
        Append("info", string.Format(
            CultureInfo.CurrentCulture,
            "Done: scanned {0}, updated {1}, no candidate {2}, locked {3}, protected {4}, fresh {5}, rate limited {6}, failed {7}.",
            sum.Scanned,
            sum.Updated,
            sum.NoCandidate,
            sum.SkippedLocked,
            sum.SkippedProtected,
            sum.SkippedFresh,
            sum.RateLimited,
            sum.Failed));
        SetPhase(string.Empty);
        progress?.Report(100);
        return false;
    }

    private List<BaseItem> GetMedia(BaseItemKind[] kinds, Guid[] libraryIds)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = kinds,
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(true)
        };

        if (libraryIds.Length > 0)
        {
            query.AncestorIds = libraryIds;
        }

        return _libraryManager.GetItemList(query).ToList();
    }

    private HashSet<Guid> LibraryIdSet()
        => _libraryManager.GetVirtualFolders()
            .Select(f => Guid.TryParse(f.ItemId, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToHashSet();

    private string[] ResolveLanguages(PluginConfiguration configuration)
    {
        if (configuration.PreferredLanguages.Length > 0)
        {
            return configuration.PreferredLanguages;
        }

        var server = _serverConfiguration.Configuration.PreferredMetadataLanguage;
        return string.IsNullOrWhiteSpace(server) ? [] : [server];
    }

    private async Task<ItemRefreshResult> ProcessItemAsync(BaseItem item, ProcessOptions options, RunContext context, CancellationToken cancellationToken)
    {
        var sem = _itemLocks.GetOrAdd(item.Id, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ProcessItemCoreAsync(item, options, context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
            if (sem.CurrentCount == 1)
            {
                _itemLocks.TryRemove(item.Id, out _);
            }
        }
    }

    private async Task<ItemRefreshResult> ProcessItemCoreAsync(BaseItem item, ProcessOptions options, RunContext context, CancellationToken cancellationToken)
    {
        var configuration = context.Configuration;
        var kind = KindOf(item);
        var result = new ItemRefreshResult { ItemId = item.Id, Name = item.Name };
        Bump(s => s.Scanned++);

        var category = IsCategory(kind);
        var libraries = category ? [] : item.GetAncestorIds().Where(context.LibraryIds.Contains).ToArray();
        var hasMarker = item.ProviderIds.ContainsKey(AutoThumbnailsMarker);
        var now = DateTimeOffset.UtcNow;

        var allowed = new List<ImageSlot>();
        foreach (var type in SlotTypesFor(kind))
        {
            if (options.Wanted is { } wanted && !wanted.Contains(type))
            {
                continue;
            }

            var slot = SlotFor(type);
            var state = State.Get(item.Id, slot.Key);
            // Only an image that is still byte-for-byte the mosaic this plugin made counts as its own;
            // the state alone proves nothing (somebody may have replaced the image since).
            var ownMosaic = OwnsCurrentImage(item, slot, state);
            var decision = ImageLockPolicy.Evaluate(
                configuration,
                new LockContext
                {
                    Kind = kind,
                    ItemId = item.Id,
                    LibraryIds = libraries,
                    NativeLocked = item.IsLocked,
                    HasAutoThumbnailsMarker = hasMarker,
                    Slot = slot,
                    SlotHasImage = item.HasImage(type, slot.Index) && !ownMosaic,
                    Force = options.Force,
                    OverrideExcludedLibraries = options.OverrideExcludedLibraries,
                    IsCategory = category
                },
                options.Mode);

            if (decision != LockDecision.Allowed)
            {
                result.Slots.Add(new SlotOutcome { Slot = slot.Key, Status = decision == LockDecision.BookPrimaryProtected ? "protected" : decision == LockDecision.AlreadyHasImage ? "has-image" : "locked", Detail = decision.ToString() });
                if (decision == LockDecision.BookPrimaryProtected)
                {
                    Bump(s => s.SkippedProtected++);
                }
                else if (decision is not (LockDecision.AlreadyHasImage or LockDecision.UnsupportedType or LockDecision.UnsupportedKind or LockDecision.NotIncluded))
                {
                    Bump(s => s.SkippedLocked++);
                }

                continue;
            }

            // Scheduled runs do not hammer: a slot that found nothing waits, and in replace mode a slot
            // refreshed within the interval is left alone, which also lets a partial run resume.
            if (options.Scheduled && !options.Force && state is not null)
            {
                var recentlyDone = options.Mode == ArtworkRefreshMode.Replace && state.LastSuccessUtc is { } ok
                                   && ok > now.AddHours(-Math.Max(1, configuration.RefreshIntervalHours));
                if (recentlyDone || (state.NextRetryUtc is { } retry && retry > now))
                {
                    result.Slots.Add(new SlotOutcome { Slot = slot.Key, Status = "fresh" });
                    Bump(s => s.SkippedFresh++);
                    continue;
                }
            }

            allowed.Add(slot);
        }

        if (allowed.Count == 0)
        {
            return result;
        }

        Bump(s => s.Eligible++);

        var query = BuildQuery(item, kind, allowed.Select(s => s.Type).ToArray(), context);
        var remote = new Dictionary<ImageType, List<ArtworkCandidate>>();
        var rateBefore = TooManyRequests();

        foreach (var sourceId in SourceSelector.Select(configuration, kind))
        {
            if (sourceId == SourceIds.LocalMosaic)
            {
                continue;
            }

            // Enough candidates to try (a found candidate is not yet a usable image): stop asking more sources.
            if (allowed.All(s => remote.TryGetValue(s.Type, out var l) && l.Count >= MaxAttemptsPerSlot))
            {
                break;
            }

            var source = Sources.FirstOrDefault(s => s.Id == sourceId);
            if (source is null)
            {
                continue;
            }

            var found = await source.FindAsync(query, Http, configuration, cancellationToken).ConfigureAwait(false);
            foreach (var c in found.Where(c => allowed.Any(s => s.Type == c.ImageType)))
            {
                if (!remote.TryGetValue(c.ImageType, out var list))
                {
                    list = [];
                    remote[c.ImageType] = list;
                }

                // Sources are asked in the configured order and each answers best first, so appending keeps
                // the priority: a later source is a real fallback when the earlier images fail to download.
                list.Add(c);
            }
        }

        foreach (var slot in allowed)
        {
            var outcome = new SlotOutcome { Slot = slot.Key };
            result.Slots.Add(outcome);
            var slotState = State.Get(item.Id, slot.Key);

            ValidatedImage? image = null;
            string? sourceUsed = null;
            string? assetId = null;
            string? mosaicSignature = null;

            if (remote.TryGetValue(slot.Type, out var candidates) && candidates.Count > 0)
            {
                var pool = candidates.AsEnumerable();
                if (options.Mode == ArtworkRefreshMode.Replace && slotState?.SourceAssetId is { } last)
                {
                    var rotated = candidates.Where(c => c.RemoteId != last).Take(Math.Max(1, configuration.ReplaceRotationPool)).ToList();
                    pool = rotated;
                    if (rotated.Count == 0)
                    {
                        outcome.Status = "fresh";
                        outcome.Detail = "no different image available";
                        Bump(s => s.SkippedFresh++);
                        continue;
                    }
                }

                foreach (var c in pool.Take(MaxAttemptsPerSlot))
                {
                    if (options.DryRun)
                    {
                        outcome.Status = "dryrun";
                        outcome.Source = c.Source;
                        outcome.Detail = ArtworkHttpClient.Redact(c.Uri);
                        Bump(s => s.Updated++);
                        sourceUsed = c.Source;
                        break;
                    }

                    var bytes = await Http.DownloadAsync(c, SettingsFor(configuration, c.Source), cancellationToken).ConfigureAwait(false);
                    if (bytes is null)
                    {
                        continue;
                    }

                    var minH = slot.Type is ImageType.Logo or ImageType.Thumb ? 50 : configuration.MinimumImageHeight;
                    image = ImageValidator.Validate(bytes, configuration.MinimumImageWidth, minH, out var why);
                    if (image is not null)
                    {
                        sourceUsed = c.Source;
                        assetId = c.RemoteId;
                        break;
                    }

                    _logger.LogInformation("Artwork Refresher: rejected an image from {Source} for {Name}: {Why}", c.Source, item.Name, why);
                }

                if (outcome.Status == "dryrun")
                {
                    continue;
                }
            }
            else if (slot.Type == ImageType.Primary && SourceSelector.Select(configuration, kind).Contains(SourceIds.LocalMosaic))
            {
                var mosaic = TryBuildMosaic(item, kind, configuration, context, slotState, options, out var skippedUnchanged);
                if (skippedUnchanged)
                {
                    outcome.Status = "fresh";
                    outcome.Detail = "mosaic unchanged";
                    Bump(s => s.SkippedFresh++);
                    continue;
                }

                if (mosaic is not null)
                {
                    if (options.DryRun)
                    {
                        outcome.Status = "dryrun";
                        outcome.Source = SourceIds.LocalMosaic;
                        Bump(s => s.Updated++);
                        continue;
                    }

                    image = mosaic.Value.Image;
                    mosaicSignature = mosaic.Value.Signature;
                    sourceUsed = SourceIds.LocalMosaic;
                }
            }

            if (image is null)
            {
                var limited = TooManyRequests() > rateBefore;
                outcome.Status = remote.ContainsKey(slot.Type) ? (limited ? "ratelimited" : "failed") : "nocandidate";
                outcome.Detail = outcome.Status == "nocandidate" ? "no source had an image" : "download or validation failed";
                if (outcome.Status == "nocandidate")
                {
                    Bump(s => s.NoCandidate++);
                }
                else if (outcome.Status == "ratelimited")
                {
                    Bump(s => s.RateLimited++);
                }
                else
                {
                    Bump(s => s.Failed++);
                }

                State.Touch(item.Id, slot.Key, st =>
                {
                    st.LastAttemptUtc = now;
                    st.FailureCount++;
                    st.NextRetryUtc = now.AddDays(Math.Max(1, configuration.RetryAfterDays) * Math.Min(4, st.FailureCount));
                });
                continue;
            }

            // The decision above is a few seconds old (downloads take time). Look again just before writing:
            // Jellyfin may have started its own refresh, an administrator may have locked the item or put
            // an image there, or the Auto Thumbnails marker may have appeared. Jellyfin offers no lock a
            // plugin can take, so this narrows the window; it cannot close it.
            if (_providerManager.GetRefreshProgress(item.Id) is not null)
            {
                outcome.Status = "fresh";
                outcome.Detail = "Jellyfin is refreshing this item; try again later";
                Bump(s => s.SkippedFresh++);
                continue;
            }

            var ownedNow = OwnsCurrentImage(item, slot, State.Get(item.Id, slot.Key));
            var recheck = ImageLockPolicy.Evaluate(
                configuration,
                new LockContext
                {
                    Kind = kind,
                    ItemId = item.Id,
                    LibraryIds = libraries,
                    NativeLocked = item.IsLocked,
                    HasAutoThumbnailsMarker = item.ProviderIds.ContainsKey(AutoThumbnailsMarker),
                    Slot = slot,
                    SlotHasImage = item.HasImage(slot.Type, slot.Index) && !ownedNow,
                    Force = options.Force,
                    OverrideExcludedLibraries = options.OverrideExcludedLibraries,
                    IsCategory = category
                },
                options.Mode);
            if (recheck != LockDecision.Allowed)
            {
                outcome.Status = recheck == LockDecision.BookPrimaryProtected ? "protected" : "locked";
                outcome.Detail = "changed while working: " + recheck;
                Bump(s => s.SkippedLocked++);
                continue;
            }

            try
            {
                await Writer.SaveAsync(item, slot, image, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            {
                outcome.Status = "failed";
                outcome.Detail = ex.Message;
                Bump(s => s.Failed++);
                continue;
            }

            var hash = Convert.ToHexString(SHA256.HashData(image.Data)).ToLowerInvariant();
            State.Touch(item.Id, slot.Key, st =>
            {
                st.LastAttemptUtc = now;
                st.LastSuccessUtc = now;
                st.Source = sourceUsed;
                st.SourceAssetId = assetId;
                st.ContentHash = hash;
                st.MosaicSignature = mosaicSignature;
                st.FailureCount = 0;
                st.NextRetryUtc = null;
            });
            outcome.Status = "updated";
            outcome.Source = sourceUsed;
            Bump(s => s.Updated++);
        }

        return result;
    }

    private static bool OwnsCurrentImage(BaseItem item, ImageSlot slot, SlotState? state)
    {
        if (state?.Source != SourceIds.LocalMosaic || string.IsNullOrEmpty(state.ContentHash) || !item.HasImage(slot.Type, slot.Index))
        {
            return false;
        }

        try
        {
            var path = item.GetImageInfo(slot.Type, slot.Index)?.Path;
            return !string.IsNullOrEmpty(path) && File.Exists(path)
                   && string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), state.ContentHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private ArtworkQuery BuildQuery(BaseItem item, BaseItemKind kind, ImageType[] types, RunContext context)
    {
        IReadOnlyDictionary<string, string> series = new Dictionary<string, string>();
        int? seasonNumber = null;
        int? episodeNumber = null;
        string? creator = null;

        switch (item)
        {
            case Season season:
                series = season.Series?.ProviderIds ?? series;
                seasonNumber = season.IndexNumber;
                break;
            case Episode episode:
                series = episode.Series?.ProviderIds ?? series;
                seasonNumber = episode.ParentIndexNumber;
                episodeNumber = episode.IndexNumber;
                break;
            case MusicAlbum album:
                creator = album.AlbumArtist;
                break;
        }

        return new ArtworkQuery
        {
            Kind = kind,
            Name = item.Name ?? string.Empty,
            Year = item.ProductionYear,
            ProviderIds = new Dictionary<string, string>(item.ProviderIds),
            SeriesProviderIds = new Dictionary<string, string>(series),
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            CreatorName = creator,
            WantedTypes = types,
            Languages = context.Languages,
            IncludeNeutral = context.Configuration.IncludeLanguageNeutralImages
        };
    }

    private (ValidatedImage Image, string Signature)? TryBuildMosaic(
        BaseItem item,
        BaseItemKind kind,
        PluginConfiguration configuration,
        RunContext context,
        SlotState? state,
        ProcessOptions options,
        out bool unchanged)
    {
        unchanged = false;
        var inputs = new List<MosaicInput>();
        foreach (var member in GetMembers(item, kind))
        {
            var info = member.GetImageInfo(ImageType.Primary, 0);
            var path = info?.Path;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                continue;
            }

            var subject = new SharedArtworkSubject(
                member.GetAncestorIds().Where(context.LibraryIds.Contains).ToArray(),
                member.Tags.Concat(member.GetInheritedTags()).ToArray(),
                member.InheritedParentalRatingValue);
            if (!SharedArtworkSafetyPolicy.IsAllowed(configuration, subject))
            {
                continue;
            }

            inputs.Add(new MosaicInput(member.Id, path + "|" + File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture), path));
        }

        if (inputs.Count < Math.Max(1, configuration.MosaicMinimumItems))
        {
            return null;
        }

        var epoch = configuration.RotateMosaics
            ? (long)(DateTimeOffset.UtcNow.UtcDateTime - DateTime.UnixEpoch).TotalDays / Math.Max(1, configuration.MosaicRotationDays)
            : 0;
        var settings = string.Join(
            ',',
            configuration.MosaicColumns,
            configuration.MosaicRows,
            configuration.MosaicWidth,
            configuration.MosaicHeight,
            configuration.JpegQuality,
            epoch);
        var signature = MosaicComposer.Signature(item.Id, inputs, settings);
        if (state?.MosaicSignature == signature && item.HasImage(ImageType.Primary, 0) && !options.Force)
        {
            unchanged = true;
            return null;
        }

        var chosen = MosaicComposer.Choose(item.Id, epoch, inputs, Math.Max(1, configuration.MosaicColumns) * Math.Max(1, configuration.MosaicRows));
        var jpeg = MosaicComposer.Render(chosen.Select(c => c.Path).ToList(), configuration.MosaicColumns, configuration.MosaicRows, configuration.MosaicWidth, configuration.MosaicHeight, configuration.JpegQuality);
        if (jpeg is null)
        {
            return null;
        }

        var image = ImageValidator.Validate(jpeg, 100, 100, out _);
        return image is null ? null : (image, signature);
    }

    private IReadOnlyList<BaseItem> GetMembers(BaseItem item, BaseItemKind kind)
    {
        switch (kind)
        {
            case BaseItemKind.Genre:
                return _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                    Genres = [item.Name],
                    Recursive = true,
                    IsVirtualItem = false,
                    DtoOptions = new DtoOptions(true)
                });
            case BaseItemKind.MusicGenre:
                return _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.MusicAlbum],
                    Genres = [item.Name],
                    Recursive = true,
                    IsVirtualItem = false,
                    DtoOptions = new DtoOptions(true)
                });
            case BaseItemKind.Studio:
                return _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                    StudioIds = [item.Id],
                    Recursive = true,
                    IsVirtualItem = false,
                    DtoOptions = new DtoOptions(true)
                });
            case BaseItemKind.BoxSet when item is BoxSet box:
                return box.GetLinkedChildren();
            default:
                return [];
        }
    }

    private static int SettingsFor(PluginConfiguration configuration, string sourceId) => sourceId switch
    {
        SourceIds.Tmdb => configuration.Sources.Tmdb.RequestsPerMinute,
        SourceIds.Fanart => configuration.Sources.Fanart.RequestsPerMinute,
        SourceIds.Wikimedia => configuration.Sources.Wikimedia.RequestsPerMinute,
        SourceIds.OpenLibrary => configuration.Sources.OpenLibrary.RequestsPerMinute,
        SourceIds.CoverArtArchive => configuration.Sources.CoverArtArchive.RequestsPerMinute,
        SourceIds.GoogleCse => configuration.Sources.GoogleCse.RequestsPerMinute,
        _ => 30
    };

    private int TooManyRequests() => Http.Counters.Values.Sum(c => c.TooManyRequests);

    private void Append(string level, string message)
    {
        lock (_sync)
        {
            _log.Add(new RunLogEntry { Sequence = ++_sequence, Timestamp = DateTime.UtcNow, Level = level, Message = message });
            if (_log.Count > MaxLogEntries)
            {
                _log.RemoveRange(0, _log.Count - MaxLogEntries);
            }
        }
    }

    private void Bump(Action<RunSummary> change)
    {
        lock (_sync)
        {
            change(_summary);
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

    private void SetFinalState(string state)
    {
        lock (_sync)
        {
            _status.State = state;
            _status.FinishedAt = DateTime.UtcNow;
            if (state.StartsWith("completed", StringComparison.Ordinal))
            {
                _status.Percent = 100;
            }
        }
    }

    private sealed record RunContext(PluginConfiguration Configuration, HashSet<Guid> LibraryIds, string[] Languages);

    private sealed class ProcessOptions
    {
        public ArtworkRefreshMode Mode { get; init; }

        public bool Force { get; init; }

        public bool DryRun { get; init; }

        public bool Scheduled { get; init; }

        public bool OverrideExcludedLibraries { get; init; }

        public ImageType[]? Wanted { get; init; }
    }
}

/// <summary>A library the user can pick.</summary>
public sealed class LibraryInfo
{
    /// <summary>Gets or sets the id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the collection type.</summary>
    public string CollectionType { get; set; } = string.Empty;
}

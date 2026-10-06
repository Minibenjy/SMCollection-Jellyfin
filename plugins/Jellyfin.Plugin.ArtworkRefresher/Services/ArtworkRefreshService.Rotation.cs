using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;
using Jellyfin.Plugin.ArtworkRefresher.Core;
using Jellyfin.Plugin.ArtworkRefresher.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArtworkRefresher.Services;

/// <summary>An image ready to be served to the browser.</summary>
/// <param name="Path">The file.</param>
/// <param name="MimeType">The MIME type.</param>
public sealed record RotatingImage(string Path, string MimeType);

/// <summary>
/// Rotation: a pool of candidate images per slot, a random draw without repeats, a daily change during the
/// pass and a different image on every page load. Also the extras of a pass (rotation, metadata gaps) and the
/// fill of items that were just added.
/// </summary>
public sealed partial class ArtworkRefreshService
{
    private static readonly ImageType[] RotatableTypes = [ImageType.Primary, ImageType.Backdrop, ImageType.Logo, ImageType.Thumb];

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _poolDownloads = new();
    private readonly PickMemo _picks = new(TimeSpan.FromMinutes(10));
    private readonly Lock _libraryCacheLock = new();
    private HashSet<Guid>? _libraryCache;
    private DateTimeOffset _libraryCacheAt;
    private long _servesSinceSave;

    /// <summary>Gets or sets the random source: returns a number from 0 (inclusive) to the argument (exclusive). Replaceable for tests.</summary>
    public Func<int, int> NextInt { get; set; } = Random.Shared.Next;

    private string PoolFolder => Path.Combine(DataFolder, "pool");

    private string OriginalsFolder => Path.Combine(DataFolder, "originals");

    /// <summary>
    /// Fills items that were just added to the library. Does nothing and returns false while a run is going,
    /// so the caller keeps them queued.
    /// </summary>
    /// <param name="ids">The item ids.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when the items were handled.</returns>
    public async Task<bool> ProcessNewItemsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        RunSummary? saved;
        lock (_sync)
        {
            saved = _summary;
            _summary = new RunSummary();
        }

        try
        {
            var configuration = Plugin.Config;
            var context = new RunContext(configuration, LibraryIdSet(), ResolveLanguages(configuration));
            var options = new ProcessOptions
            {
                Mode = ArtworkRefreshMode.MissingOnly,
                DryRun = configuration.DryRun,
                Scheduled = false,
                FillMetadata = configuration.FillMetadataGaps
            };

            foreach (var id in ids)
            {
                var item = _libraryManager.GetItemById(id);
                if (item is null)
                {
                    continue;
                }

                try
                {
                    var result = await ProcessItemAsync(item, options, context, cancellationToken).ConfigureAwait(false);
                    foreach (var s in result.Slots.Where(s => s.Status is "updated" or "dryrun"))
                    {
                        Append("ok", "New item " + item.Name + " — " + s.Slot + " (" + s.Source + ")");
                    }

                    await RunExtrasAsync(item, options, context, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Artwork Refresher: failed on the new item {Name}", item.Name);
                }
            }

            await State.SaveAsync(true).ConfigureAwait(false);
            return true;
        }
        finally
        {
            lock (_sync)
            {
                _summary = saved;
            }

            _runGate.Release();
        }
    }

    /// <summary>
    /// Serves a different image of the pool on each call (rotation per page load).
    /// </summary>
    /// <param name="id">The item id.</param>
    /// <param name="type">The image type.</param>
    /// <param name="token">The signed token the "available" call handed out.</param>
    /// <param name="nonce">An id of this page view: the same nonce always gets the same image (preload and display agree).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The image, or null when this slot does not rotate or the token is wrong (the client keeps the normal image).</returns>
    public async Task<RotatingImage?> ServeRotatingAsync(Guid id, ImageType type, string? token, string? nonce, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Config;
        if (!configuration.Enabled || configuration.DryRun || RotationPolicy.ModeFor(configuration, type) != RotationMode.PerLoad)
        {
            return null;
        }

        if (!RotationSigner.Validate(State.Use(d => d.SigningKey), id, type.ToString(), token, DateTimeOffset.UtcNow))
        {
            return null;
        }

        if (!IsRotationAllowed(id, type, configuration, out var slot))
        {
            return null;
        }

        var memoKey = string.IsNullOrEmpty(nonce) ? null : id.ToString("N", CultureInfo.InvariantCulture) + ":" + slot.Key + ":" + (nonce.Length > 40 ? nonce[..40] : nonce);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            PoolEntry? entry = null;
            string? remembered = null;
            if (attempt == 0 && memoKey is not null && _picks.TryGet(memoKey, DateTimeOffset.UtcNow, out var rememberedId))
            {
                remembered = rememberedId;
            }

            State.Update(d =>
            {
                if (!d.Pools.TryGetValue(id, out var slots) || !slots.TryGetValue(slot.Key, out var pool))
                {
                    return;
                }

                var usable = pool.Entries.Where(e => e.Failures < 3).ToList();
                if (usable.Count < RotationPolicy.MinPool)
                {
                    return;
                }

                if (remembered is not null && usable.FirstOrDefault(e => e.AssetId == remembered) is { } same)
                {
                    entry = same;
                    return;
                }

                var pick = RotationPolicy.Pick(usable.Select(e => e.AssetId).ToList(), pool.History, pool.History.Count > 0 ? pool.History[^1] : null, NextInt);
                entry = pick is null ? null : usable.First(e => e.AssetId == pick.AssetId);
                d.Scheduler.PerLoadServes++;
            });
            State.MarkPoolsDirty();

            if (entry is null)
            {
                return null;
            }

            var cached = await EnsureCachedAsync(id, slot, entry, configuration, cancellationToken).ConfigureAwait(false);
            if (cached is null)
            {
                continue;
            }

            if (memoKey is not null)
            {
                _picks.Set(memoKey, entry.AssetId, DateTimeOffset.UtcNow);
            }

            if (Interlocked.Increment(ref _servesSinceSave) % 50 == 0)
            {
                // Keeps the history across a restart without writing the state on every page load.
                await State.SaveAsync().ConfigureAwait(false);
                PruneCache(configuration);
            }

            return cached;
        }

        return null;
    }

    /// <summary>
    /// Tells which of the given items have rotating images and gives a signed token for each.
    /// </summary>
    /// <param name="ids">The item ids (at most 300 are looked at).</param>
    /// <returns>Keys in the form id:Type (id as 32 hex digits) with their token.</returns>
    public IReadOnlyDictionary<string, string> RotatableTokens(IEnumerable<Guid> ids)
    {
        var configuration = Plugin.Config;
        var keys = new Dictionary<string, string>();
        if (!configuration.Enabled || configuration.DryRun)
        {
            return keys;
        }

        var types = RotatableTypes.Where(t => RotationPolicy.ModeFor(configuration, t) == RotationMode.PerLoad).ToArray();
        if (types.Length == 0)
        {
            return keys;
        }

        var signingKey = State.Use(d => d.SigningKey ??= RotationSigner.NewKey());
        var now = DateTimeOffset.UtcNow;
        foreach (var id in ids.Take(300))
        {
            foreach (var type in types)
            {
                if (IsRotationAllowed(id, type, configuration, out var slot)
                    && State.GetPool(id, slot.Key) is { } pool && pool.Entries.Count(e => e.Failures < 3) >= RotationPolicy.MinPool)
                {
                    keys[id.ToString("N", CultureInfo.InvariantCulture) + ":" + type] = RotationSigner.Sign(signingKey, id, type.ToString(), now);
                }
            }
        }

        return keys;
    }

    /// <summary>
    /// Puts back the image an item had before the first daily rotation (from the backup) and adds an item
    /// lock for that image type so the rotation does not change it again.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="type">The image type.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when restored; false when there is no backup.</returns>
    public async Task<bool> RestoreOriginalAsync(BaseItem item, ImageType type, CancellationToken cancellationToken)
    {
        var slot = SlotFor(type);
        var pool = State.GetPool(item.Id, slot.Key);
        if (pool?.OriginalBackup is null)
        {
            return false;
        }

        var path = Path.Combine(OriginalsFolder, pool.OriginalBackup);
        if (!File.Exists(path))
        {
            return false;
        }

        var configuration = Plugin.Config;
        var image = ImageValidator.Validate(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), 1, 1, out _);
        if (image is null)
        {
            return false;
        }

        await WithItemLockAsync<bool>(
            item.Id,
            async () =>
            {
                await Writer.SaveAsync(item, slot, image, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexString(SHA256.HashData(image.Data)).ToLowerInvariant();
        State.TouchPool(item.Id, slot.Key, p =>
        {
            p.WrittenHash = hash;
            p.ExternalChange = false;
            p.LastRotationDay = LocalDay(configuration);
        });
        State.Touch(item.Id, slot.Key, st =>
        {
            st.Source = "Original";
            st.SourceAssetId = null;
            st.ContentHash = hash;
        });
        var plugin = Plugin.Instance;
        if (plugin is not null)
        {
            var typeName = type.ToString();
            plugin.Configuration.ImageLocks = plugin.Configuration.ImageLocks
                .Where(r => !(r.TargetId == item.Id && string.Equals(r.ImageType, typeName, StringComparison.OrdinalIgnoreCase)))
                .Append(new ImageLockRule { TargetId = item.Id, IsLibrary = false, ImageType = typeName, Note = "original restored" })
                .ToArray();
            plugin.Save();
        }

        await State.SaveAsync(true).ConfigureAwait(false);
        return true;
    }

    /// <summary>Deletes the oldest cached pool images when the cache is over its limit.</summary>
    /// <param name="configuration">The configuration.</param>
    public void PruneCache(PluginConfiguration configuration)
    {
        try
        {
            if (!Directory.Exists(PoolFolder))
            {
                return;
            }

            var limit = Math.Max(16L, configuration.PoolCacheMaxMegabytes) * 1024 * 1024;
            var files = new DirectoryInfo(PoolFolder).GetFiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            if (total <= limit)
            {
                return;
            }

            var removed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (total <= limit * 0.9)
                {
                    break;
                }

                total -= file.Length;
                removed.Add(file.Name);
                file.Delete();
            }

            State.Update(d =>
            {
                foreach (var slots in d.Pools.Values)
                {
                    foreach (var st in slots.Values)
                    {
                        foreach (var e in st.Entries)
                        {
                            if (e.CachedFile is not null && removed.Contains(e.CachedFile))
                            {
                                e.CachedFile = null;
                            }
                        }
                    }
                }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Artwork Refresher: could not prune the pool cache");
        }
    }

    private static bool RotatableKind(BaseItemKind kind)
        => kind is BaseItemKind.Movie or BaseItemKind.Series or BaseItemKind.Season or BaseItemKind.Episode
            or BaseItemKind.MusicAlbum or BaseItemKind.MusicArtist;

    private static string AssetIdOf(ArtworkCandidate c)
        => !string.IsNullOrEmpty(c.RemoteId) ? c.RemoteId : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.Uri.AbsoluteUri)))[..16].ToLowerInvariant();

    private static ArtworkCandidate ToCandidate(PoolEntry e, ImageType type)
        => new()
        {
            Source = e.Source,
            RemoteId = e.AssetId,
            Uri = new Uri(e.Uri),
            ImageType = type,
            Width = e.Width,
            Height = e.Height,
            Language = e.Language,
            AnyPublicHost = e.AnyPublicHost
        };

    private async Task RunExtrasAsync(BaseItem item, ProcessOptions options, RunContext context, CancellationToken cancellationToken)
    {
        if (RotationPolicy.AnyEnabled(context.Configuration))
        {
            await WithItemLockAsync<bool>(item.Id, () => ProcessRotationAsync(item, options, context, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        if (options.FillMetadata)
        {
            await WithItemLockAsync<bool>(item.Id, () => ProcessMetadataAsync(item, options, context, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    private HashSet<Guid> CachedLibraryIds()
    {
        lock (_libraryCacheLock)
        {
            if (_libraryCache is null || DateTimeOffset.UtcNow - _libraryCacheAt > TimeSpan.FromMinutes(2))
            {
                _libraryCache = LibraryIdSet();
                _libraryCacheAt = DateTimeOffset.UtcNow;
            }

            return _libraryCache;
        }
    }

    private LockDecision RotationDecision(BaseItem item, BaseItemKind kind, ImageSlot slot, PluginConfiguration configuration, HashSet<Guid> libraryIds)
        => ImageLockPolicy.Evaluate(
            configuration,
            new LockContext
            {
                Kind = kind,
                ItemId = item.Id,
                LibraryIds = item.GetAncestorIds().Where(libraryIds.Contains).ToArray(),
                NativeLocked = item.IsLocked,
                HasAutoThumbnailsMarker = item.ProviderIds.ContainsKey(AutoThumbnailsMarker),
                Slot = slot,
                SlotHasImage = item.HasImage(slot.Type, slot.Index),
                IsCategory = false
            },
            ArtworkRefreshMode.Replace);

    private bool IsRotationAllowed(Guid id, ImageType type, PluginConfiguration configuration, out ImageSlot slot)
    {
        slot = SlotFor(type);
        var item = _libraryManager.GetItemById(id);
        if (item is null)
        {
            return false;
        }

        var kind = KindOf(item);
        return RotatableKind(kind) && SlotTypesFor(kind).Contains(type)
               && RotationDecision(item, kind, slot, configuration, CachedLibraryIds()) == LockDecision.Allowed;
    }

    private async Task<RotatingImage?> EnsureCachedAsync(Guid id, ImageSlot slot, PoolEntry entry, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        if (entry.CachedFile is not null)
        {
            var existing = Path.Combine(PoolFolder, entry.CachedFile);
            if (File.Exists(existing))
            {
                return new RotatingImage(existing, entry.MimeType ?? "image/jpeg");
            }
        }

        if (!SourceHelpersTryHttps(entry.Uri, out var uri))
        {
            return null;
        }

        var key = id.ToString("N", CultureInfo.InvariantCulture) + ":" + slot.Key + ":" + entry.AssetId;
        var gate = _poolDownloads.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry.CachedFile is not null && File.Exists(Path.Combine(PoolFolder, entry.CachedFile)))
            {
                return new RotatingImage(Path.Combine(PoolFolder, entry.CachedFile), entry.MimeType ?? "image/jpeg");
            }

            var candidate = new ArtworkCandidate
            {
                Source = entry.Source,
                RemoteId = entry.AssetId,
                Uri = uri,
                ImageType = slot.Type,
                Width = entry.Width,
                Height = entry.Height,
                Language = entry.Language,
                AnyPublicHost = entry.AnyPublicHost
            };

            var bytes = await Http.DownloadAsync(candidate, SettingsFor(configuration, entry.Source), cancellationToken).ConfigureAwait(false);
            var minH = slot.Type is ImageType.Logo or ImageType.Thumb ? 50 : configuration.MinimumImageHeight;
            var image = bytes is null ? null : ImageValidator.Validate(bytes, configuration.MinimumImageWidth, minH, out _);
            if (image is null)
            {
                State.Update(_ => entry.Failures++);
                State.MarkPoolsDirty();
                return null;
            }

            Directory.CreateDirectory(PoolFolder);
            var name = id.ToString("N", CultureInfo.InvariantCulture) + "_" + slot.Type + "_" + Convert.ToHexString(SHA256.HashData(image.Data))[..12].ToLowerInvariant() + image.Extension;
            var path = Path.Combine(PoolFolder, name);
            await File.WriteAllBytesAsync(path, image.Data, cancellationToken).ConfigureAwait(false);
            State.Update(_ =>
            {
                entry.CachedFile = name;
                entry.MimeType = image.MimeType;
                entry.Failures = 0;
            });
            State.MarkPoolsDirty();
            return new RotatingImage(path, image.MimeType);
        }
        finally
        {
            gate.Release();
            if (gate.CurrentCount == 1)
            {
                _poolDownloads.TryRemove(key, out _);
            }
        }
    }

    private static bool SourceHelpersTryHttps(string text, out Uri uri) => Jellyfin.Plugin.ArtworkRefresher.Sources.SourceHelpers.TryHttps(text, out uri);

    private static string? HashOfCurrentImage(BaseItem item, ImageSlot slot)
    {
        try
        {
            var path = item.HasImage(slot.Type, slot.Index) ? item.GetImageInfo(slot.Type, slot.Index)?.Path : null;
            return !string.IsNullOrEmpty(path) && File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private string? BackupOriginal(BaseItem item, ImageSlot slot)
    {
        try
        {
            var path = item.HasImage(slot.Type, slot.Index) ? item.GetImageInfo(slot.Type, slot.Index)?.Path : null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            Directory.CreateDirectory(OriginalsFolder);
            var name = item.Id.ToString("N", CultureInfo.InvariantCulture) + "_" + slot.Type + Path.GetExtension(path).ToLowerInvariant();
            File.Copy(path, Path.Combine(OriginalsFolder, name), true);
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Artwork Refresher: could not back up the original image of {Name}", item.Name);
            return null;
        }
    }

    private static string LocalDay(PluginConfiguration configuration)
        => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, RefreshWindowPolicy.ResolveZone(configuration.RefreshWindowTimeZone)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<bool> ProcessRotationAsync(BaseItem item, ProcessOptions options, RunContext context, CancellationToken cancellationToken)
    {
        var configuration = context.Configuration;
        var kind = KindOf(item);
        if (!RotatableKind(kind))
        {
            return true;
        }

        var slots = new List<(ImageSlot Slot, RotationMode Mode, int Size)>();
        foreach (var type in SlotTypesFor(kind))
        {
            var mode = RotationPolicy.ModeFor(configuration, type);
            if (mode == RotationMode.Off || (options.Wanted is { } w && !w.Contains(type)))
            {
                continue;
            }

            var slot = SlotFor(type);
            if (RotationDecision(item, kind, slot, configuration, context.LibraryIds) == LockDecision.Allowed)
            {
                slots.Add((slot, mode, RotationPolicy.PoolSizeFor(configuration, type)));
            }
        }

        if (slots.Count == 0)
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        var pools = new Dictionary<string, List<PoolEntry>>();
        var needSearch = slots.Where(s =>
        {
            var st = State.GetPool(item.Id, s.Slot.Key);
            if (st?.BuiltUtc is null)
            {
                return true;
            }

            var age = now - st.BuiltUtc.Value;
            return age > TimeSpan.FromDays(Math.Max(1, configuration.PoolRefreshDays))
                   || (st.Entries.Count < RotationPolicy.MinPool && age > TimeSpan.FromDays(Math.Max(1, configuration.RetryAfterDays)));
        }).ToList();

        if (needSearch.Count > 0)
        {
            var types = needSearch.Select(s => s.Slot.Type).ToArray();
            var query = BuildQuery(item, kind, types, context);
            var maxSize = needSearch.Max(s => s.Size);
            var remote = await GatherCandidatesAsync(query, kind, types, configuration, maxSize + 3, cancellationToken).ConfigureAwait(false);
            foreach (var (slot, _, size) in needSearch)
            {
                var found = (remote.TryGetValue(slot.Type, out var list) ? list : [])
                    .Select(c => new PoolEntry
                    {
                        AssetId = AssetIdOf(c),
                        Source = c.Source,
                        Uri = c.Uri.AbsoluteUri,
                        AnyPublicHost = c.AnyPublicHost,
                        Width = c.Width,
                        Height = c.Height,
                        Language = c.Language
                    })
                    .ToList();
                var stored = State.GetPool(item.Id, slot.Key);
                var current = State.Get(item.Id, slot.Key)?.SourceAssetId;
                var merged = RotationPolicy.Merge(stored?.Entries, found, size, current);
                pools[slot.Key] = merged;
                if (options.DryRun)
                {
                    Append("skip", item.Name + " — " + slot.Key + ": would keep a pool of " + merged.Count + " image(s)");
                    continue;
                }

                var dropped = (stored?.Entries ?? []).Where(e => merged.All(m => m.AssetId != e.AssetId) && e.CachedFile is not null).Select(e => e.CachedFile!).ToList();
                State.TouchPool(item.Id, slot.Key, st =>
                {
                    st.Entries = merged;
                    st.BuiltUtc = now;
                    st.History = st.History.Where(h => merged.Any(m => m.AssetId == h)).ToList();
                });
                foreach (var file in dropped)
                {
                    try
                    {
                        File.Delete(Path.Combine(PoolFolder, file));
                    }
                    catch (IOException)
                    {
                        // The cache limit removes it later.
                    }
                }

                Bump(sum => sum.PoolsBuilt++);
            }
        }

        var today = LocalDay(configuration);
        foreach (var (slot, mode, _) in slots)
        {
            if (mode != RotationMode.Daily)
            {
                continue;
            }

            var state = State.GetPool(item.Id, slot.Key);
            if (state?.LastRotationDay == today)
            {
                continue;
            }

            var pool = pools.TryGetValue(slot.Key, out var fresh) ? fresh : state?.Entries;
            if (pool is null || pool.Count(e => e.Failures < 3) < RotationPolicy.MinPool)
            {
                continue;
            }

            await RotateDailyAsync(item, slot, pool, state, options, context, today, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task RotateDailyAsync(BaseItem item, ImageSlot slot, List<PoolEntry> pool, PoolState? state, ProcessOptions options, RunContext context, string today, CancellationToken cancellationToken)
    {
        var configuration = context.Configuration;
        var history = new List<string>(state?.History ?? []);
        var current = State.Get(item.Id, slot.Key)?.SourceAssetId;
        var kind = KindOf(item);

        // An image this rotation wrote earlier must still be the one on the item. If somebody replaced it
        // since (a manual upload, another plugin), it is theirs now and the rotation leaves it alone.
        if (state?.WrittenHash is { } written)
        {
            var currentHash = HashOfCurrentImage(item, slot);
            if (currentHash is not null && !string.Equals(currentHash, written, StringComparison.OrdinalIgnoreCase))
            {
                if (!options.DryRun && !state.ExternalChange)
                {
                    State.TouchPool(item.Id, slot.Key, p => p.ExternalChange = true);
                }

                Bump(s => s.SkippedLocked++);
                Append("skip", item.Name + " — " + slot.Key + ": the image was changed outside this plugin; rotation left it alone");
                return;
            }
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var usable = pool.Where(e => e.Failures < 3).ToList();
            var pick = RotationPolicy.Pick(usable.Select(e => e.AssetId).ToList(), history, current, NextInt);
            if (pick is null)
            {
                return;
            }

            var entry = usable.First(e => e.AssetId == pick.AssetId);
            if (options.DryRun)
            {
                Bump(s => s.RotationDryRun++);
                Append("skip", item.Name + " — " + slot.Key + ": would change to " + entry.Source + " " + ArtworkHttpClient.Redact(new Uri(entry.Uri)));
                return;
            }

            if (!SourceHelpersTryHttps(entry.Uri, out var uri))
            {
                entry.Failures++;
                continue;
            }

            var bytes = await Http.DownloadAsync(ToCandidate(new PoolEntry { AssetId = entry.AssetId, Source = entry.Source, Uri = uri.AbsoluteUri, AnyPublicHost = entry.AnyPublicHost, Width = entry.Width, Height = entry.Height, Language = entry.Language }, slot.Type), SettingsFor(configuration, entry.Source), cancellationToken).ConfigureAwait(false);
            var minH = slot.Type is ImageType.Logo or ImageType.Thumb ? 50 : configuration.MinimumImageHeight;
            var image = bytes is null ? null : ImageValidator.Validate(bytes, configuration.MinimumImageWidth, minH, out _);
            if (image is null)
            {
                entry.Failures++;
                continue;
            }

            if (_providerManager.GetRefreshProgress(item.Id) is not null
                || RotationDecision(item, kind, slot, configuration, context.LibraryIds) != LockDecision.Allowed)
            {
                Bump(s => s.SkippedFresh++);
                return;
            }

            string? backup = state?.OriginalBackup;
            if (backup is null && configuration.BackupOriginalImages)
            {
                backup = BackupOriginal(item, slot);
            }

            try
            {
                await Writer.SaveAsync(item, slot, image, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
            {
                Bump(s => s.Failed++);
                Append("error", item.Name + " — " + slot.Key + ": " + ex.Message);
                return;
            }

            var hash = Convert.ToHexString(SHA256.HashData(image.Data)).ToLowerInvariant();
            var when = DateTimeOffset.UtcNow;
            State.Touch(item.Id, slot.Key, st =>
            {
                st.LastAttemptUtc = when;
                st.LastSuccessUtc = when;
                st.Source = entry.Source;
                st.SourceAssetId = entry.AssetId;
                st.ContentHash = hash;
                st.MosaicSignature = null;
                st.FailureCount = 0;
                st.NextRetryUtc = null;
            });
            State.TouchPool(item.Id, slot.Key, p =>
            {
                p.Entries = pool;
                p.History = history;
                p.LastRotationDay = today;
                p.RotationChanges++;
                p.WrittenHash = hash;
                p.ExternalChange = false;
                p.OriginalBackup = backup;
            });
            Bump(s => s.RotationChanges++);
            Append("ok", item.Name + " — " + slot.Key + " rotated (" + entry.Source + (pick.HistoryReset ? ", cycle restarted" : string.Empty) + ")");
            return;
        }
    }
}

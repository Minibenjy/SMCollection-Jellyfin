using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.MatureContent.Configuration;
using Jellyfin.Plugin.MatureContent.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MatureContent.Services;

/// <summary>
/// Applies Jellyfin-native blocked tag policies for mature content and keeps a
/// durable history of every item that has been marked mature.
/// </summary>
public class MaturePolicyService
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MaturePolicyService> _logger;
    private readonly SemaphoreSlim _historyLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="MaturePolicyService"/> class.
    /// </summary>
    /// <param name="userManager">The Jellyfin user manager.</param>
    /// <param name="libraryManager">The Jellyfin library manager.</param>
    /// <param name="logger">The logger.</param>
    public MaturePolicyService(
        IUserManager userManager,
        ILibraryManager libraryManager,
        ILogger<MaturePolicyService> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets all configured mature tag aliases.
    /// </summary>
    public static IReadOnlyList<string> Tags
    {
        get
        {
            var configured = Plugin.Config.MatureTags ?? [];
            var tags = configured
                .Concat(["mature", "+18"])
                .Select(t => (t ?? string.Empty).Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return tags.Length == 0 ? ["mature", "+18"] : tags;
        }
    }

    // ---------------------------------------------------------------- users

    /// <summary>
    /// Gets all users with their current mature state.
    /// </summary>
    /// <returns>User rows.</returns>
    public IReadOnlyList<MatureUserInfo> GetUsers()
        => _userManager.GetUsers()
            .Select(user =>
            {
                var policy = GetPolicy(user);
                return new MatureUserInfo
                {
                    Id = user.Id.ToString("N"),
                    Username = user.Username,
                    IsAdministrator = policy.IsAdministrator,
                    MatureVisible = IsMatureVisible(policy)
                };
            })
            .OrderByDescending(u => u.IsAdministrator)
            .ThenBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Gets the effective state for a user, applying forced defaults when needed.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The mature state.</returns>
    public async Task<MatureState> GetStateAsync(User user)
    {
        var policy = GetPolicy(user);
        var canToggle = CanToggle(user.Id, policy);
        var visible = IsMatureVisible(policy);

        if (!canToggle)
        {
            var forcedVisible = ForcedVisible(user.Id);
            if (visible != forcedVisible)
            {
                await SetMatureVisibleAsync(user, forcedVisible).ConfigureAwait(false);
                visible = forcedVisible;
            }
        }

        return new MatureState
        {
            CanToggle = canToggle,
            MatureVisible = visible,
            Locked = !canToggle,
            MatureTags = Tags.ToArray()
        };
    }

    /// <summary>
    /// Updates mature visibility for a user by id (used by the admin config page).
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="visible">Whether mature content should be visible.</param>
    /// <returns>The refreshed user row, or <c>null</c> when the user is unknown.</returns>
    public async Task<MatureUserInfo?> SetUserVisibleAsync(Guid userId, bool visible)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return null;
        }

        await SetMatureVisibleAsync(user, visible).ConfigureAwait(false);
        var policy = GetPolicy(user);
        return new MatureUserInfo
        {
            Id = user.Id.ToString("N"),
            Username = user.Username,
            IsAdministrator = policy.IsAdministrator,
            MatureVisible = visible // reflect the value we just wrote; the policy read-back can lag within the request
        };
    }

    /// <summary>
    /// Updates mature visibility for a user.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="visible">Whether mature content should be visible.</param>
    /// <returns>A task representing the update.</returns>
    public async Task SetMatureVisibleAsync(User user, bool visible)
    {
        var policy = GetPolicy(user);
        var blocked = new HashSet<string>(policy.BlockedTags ?? [], StringComparer.OrdinalIgnoreCase);

        foreach (var tag in Tags)
        {
            if (visible)
            {
                blocked.Remove(tag);
            }
            else
            {
                blocked.Add(tag);
            }
        }

        policy.BlockedTags = blocked.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();
        await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies configured locked defaults to all users that do not have a toggle.
    /// </summary>
    /// <returns>A task representing the update.</returns>
    public async Task ApplyLockedDefaultsAsync()
    {
        foreach (var user in _userManager.GetUsers())
        {
            try
            {
                var policy = GetPolicy(user);
                if (!CanToggle(user.Id, policy))
                {
                    await SetMatureVisibleAsync(user, ForcedVisible(user.Id)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mature Content: failed to apply locked default for user {UserId}.", user.Id);
            }
        }
    }

    // ---------------------------------------------------------------- items

    /// <summary>
    /// Gets mature tag state for an item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <returns>The item state, or <c>null</c> when not found.</returns>
    public MatureItemState? GetItemState(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        return item is null ? null : ToItemState(item);
    }

    /// <summary>
    /// Adds or removes the primary mature tag from an item and records it in the durable history.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="isMature">Whether the item should be marked as mature.</param>
    /// <returns>The updated item state, or <c>null</c> when not found.</returns>
    public async Task<MatureItemState?> SetItemMatureAsync(Guid itemId, bool isMature)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        await ApplyTagAsync(item, isMature).ConfigureAwait(false);
        await UpdateHistoryAsync(itemId, isMature).ConfigureAwait(false);
        return ToItemState(item);
    }

    /// <summary>
    /// Returns the current mature-marked catalogue: every id in the durable history,
    /// resolved against the library (missing items are flagged, not dropped).
    /// </summary>
    /// <returns>Item rows ordered by name.</returns>
    public IReadOnlyList<MatureItemState> GetMarkedItems()
    {
        var rows = new List<MatureItemState>();
        foreach (var id in Plugin.Config.MarkedItemIds ?? [])
        {
            if (!Guid.TryParse(id, out var guid))
            {
                continue;
            }

            var item = _libraryManager.GetItemById(guid);
            if (item is null)
            {
                rows.Add(new MatureItemState { Id = guid.ToString("N"), Name = "(elemento eliminado)", Exists = false });
                continue;
            }

            rows.Add(ToItemState(item));
        }

        return rows
            .OrderByDescending(r => r.Exists)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Reconciles the durable history with the actual library state:
    /// discovers items that already carry a mature tag, re-applies the tag to
    /// history entries that lost it, and drops ids that no longer resolve.
    /// </summary>
    /// <param name="progress">Optional progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary of what changed.</returns>
    public async Task<MatureSyncResult> SyncAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        await _historyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var known = new HashSet<Guid>(
                (Plugin.Config.MarkedItemIds ?? [])
                    .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
                    .Where(g => g != Guid.Empty));

            var result = new MatureSyncResult();

            // 1. Discover items that already carry a mature tag anywhere in the library.
            //    Restrict to concrete media kinds: a bare Recursive query trips over
            //    legacy/live-tv rows that the repository cannot deserialize.
            var all = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes =
                [
                    BaseItemKind.Movie,
                    BaseItemKind.Series,
                    BaseItemKind.Season,
                    BaseItemKind.Episode,
                    BaseItemKind.BoxSet,
                    BaseItemKind.MusicAlbum,
                    BaseItemKind.MusicArtist,
                    BaseItemKind.MusicVideo,
                    BaseItemKind.Video,
                    BaseItemKind.Book,
                    BaseItemKind.AudioBook,
                    BaseItemKind.Photo,
                    BaseItemKind.PhotoAlbum,
                    BaseItemKind.Folder
                ]
            });
            var tagged = new HashSet<Guid>();

            // Library root folders (CollectionFolder) are not returned by GetItemList.
            try
            {
                foreach (var folder in _libraryManager.RootFolder.Children)
                {
                    if (HasMatureTag(folder))
                    {
                        tagged.Add(folder.Id);
                        if (known.Add(folder.Id))
                        {
                            result.Discovered++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Mature Content: could not enumerate root folders during sync.");
            }

            var count = all.Count;
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = all[i];
                if (HasMatureTag(item))
                {
                    tagged.Add(item.Id);
                    if (known.Add(item.Id))
                    {
                        result.Discovered++;
                    }
                }

                progress?.Report(i * 90.0 / Math.Max(count, 1));
            }

            // 2. Re-apply the tag to history entries whose tag went missing; drop dead ids.
            var final = new List<Guid>();
            foreach (var id in known)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = _libraryManager.GetItemById(id);
                if (item is null)
                {
                    result.Removed++;
                    continue;
                }

                final.Add(id);
                if (!tagged.Contains(id) && !HasMatureTag(item))
                {
                    await ApplyTagAsync(item, true).ConfigureAwait(false);
                    result.Reapplied++;
                }
            }

            Plugin.Config.MarkedItemIds = final.Select(g => g.ToString("N")).OrderBy(s => s).ToArray();
            Plugin.Instance?.Save();
            result.Total = final.Count;
            progress?.Report(100);
            _logger.LogInformation(
                "Mature Content sync: total {Total}, discovered {Discovered}, reapplied {Reapplied}, removed {Removed}.",
                result.Total,
                result.Discovered,
                result.Reapplied,
                result.Removed);
            return result;
        }
        finally
        {
            _historyLock.Release();
        }
    }

    /// <summary>
    /// Re-applies the mature tag to every item in the durable history that is missing it.
    /// Cheap enough to run on startup and after every library scan.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of items that were repaired.</returns>
    public async Task<int> ReapplyMarksAsync(CancellationToken cancellationToken = default)
    {
        var ids = (Plugin.Config.MarkedItemIds ?? []).ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        var repaired = 0;
        var stillPresent = new List<string>();
        foreach (var raw in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParse(raw, out var guid))
            {
                continue;
            }

            var item = _libraryManager.GetItemById(guid);
            if (item is null)
            {
                continue;
            }

            stillPresent.Add(guid.ToString("N"));
            if (!HasMatureTag(item))
            {
                try
                {
                    await ApplyTagAsync(item, true).ConfigureAwait(false);
                    repaired++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Mature Content: could not re-apply tag to {ItemId}.", guid);
                }
            }
        }

        if (repaired > 0)
        {
            _logger.LogInformation("Mature Content: re-applied mature tag to {Count} item(s) from history.", repaired);
        }

        return repaired;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Gets whether the user can toggle mature content.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="policy">The user's policy.</param>
    /// <returns>Whether the toggle is allowed.</returns>
    public bool CanToggle(Guid userId, UserPolicy policy)
    {
        var config = Plugin.Config;
        var rule = config.GetRule(userId);
        if (rule is not null)
        {
            return rule.ShowToggle;
        }

        return policy.IsAdministrator ? config.ShowToggleForAdministrators : config.ShowToggleForUsers;
    }

    private static bool HasMatureTag(BaseItem item)
    {
        var tags = item.Tags ?? [];
        return Tags.Any(tag => tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
    }

    private async Task ApplyTagAsync(BaseItem item, bool isMature)
    {
        var tags = new HashSet<string>(item.Tags ?? [], StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var tag in Tags)
        {
            if (tags.Remove(tag))
            {
                changed = true;
            }
        }

        if (isMature)
        {
            tags.Add(Tags[0]);
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        item.Tags = tags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();
        var parent = item.GetParent();
        if (parent is not null)
        {
            await _libraryManager.UpdateItemAsync(item, parent, ItemUpdateType.MetadataEdit, default).ConfigureAwait(false);
        }
        else
        {
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, default).ConfigureAwait(false);
        }
    }

    private async Task UpdateHistoryAsync(Guid itemId, bool isMature)
    {
        await _historyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var set = new HashSet<string>(Plugin.Config.MarkedItemIds ?? [], StringComparer.OrdinalIgnoreCase);
            var key = itemId.ToString("N");
            var changed = isMature ? set.Add(key) : set.Remove(key);
            if (!changed)
            {
                return;
            }

            Plugin.Config.MarkedItemIds = set.OrderBy(s => s).ToArray();
            Plugin.Instance?.Save();
        }
        finally
        {
            _historyLock.Release();
        }
    }

    private static bool ForcedVisible(Guid userId)
    {
        var config = Plugin.Config;
        return config.GetRule(userId)?.DefaultVisibleWhenToggleHidden ?? config.DefaultVisibleWhenToggleHidden;
    }

    private static bool IsMatureVisible(UserPolicy policy)
    {
        var blocked = policy.BlockedTags ?? [];
        return !Tags.Any(tag => blocked.Contains(tag, StringComparer.OrdinalIgnoreCase));
    }

    private static MatureItemState ToItemState(BaseItem item)
        => new MatureItemState
        {
            Id = item.Id.ToString("N"),
            Name = item.Name ?? item.Id.ToString("N"),
            Tags = item.Tags ?? [],
            Type = item.GetType().Name,
            Path = item.Path ?? string.Empty,
            Exists = true,
            IsMature = HasMatureTag(item)
        };

    private UserPolicy GetPolicy(User user)
    {
        var dto = _userManager.GetUserDto(user, string.Empty);
        return dto.Policy ?? new UserPolicy();
    }
}

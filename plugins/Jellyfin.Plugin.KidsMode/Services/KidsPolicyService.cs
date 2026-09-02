using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.KidsMode.Configuration;
using Jellyfin.Plugin.KidsMode.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KidsMode.Services;

/// <summary>
/// Kids mode: an allow-list view built on Jellyfin's native <see cref="UserPolicy.AllowedTags"/>
/// plus a folder restriction, with an admin-curated global list and per-user overrides.
/// </summary>
public class KidsPolicyService
{
    private static readonly BaseItemKind[] _kinds =
    [
        BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode,
        BaseItemKind.BoxSet, BaseItemKind.MusicAlbum, BaseItemKind.MusicArtist, BaseItemKind.MusicVideo,
        BaseItemKind.Video, BaseItemKind.Book, BaseItemKind.AudioBook, BaseItemKind.Photo,
        BaseItemKind.PhotoAlbum, BaseItemKind.Folder
    ];

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<KidsPolicyService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="KidsPolicyService"/> class.
    /// </summary>
    /// <param name="userManager">User manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public KidsPolicyService(IUserManager userManager, ILibraryManager libraryManager, ILogger<KidsPolicyService> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    private static StringComparer OIC => StringComparer.OrdinalIgnoreCase;

    /// <summary>Gets the human-visible marker tag.</summary>
    public static string MarkerTag
    {
        get
        {
            var t = (Plugin.Config.KidsTag ?? "kids").Trim();
            return t.Length == 0 ? "kids" : t;
        }
    }

    private static string UserTag(Guid userId) => "kids_" + userId.ToString("N")[..8];

    private static string[] Ids(string[]? a) => a ?? [];

    /// <summary>Gets whether kids mode is available (offered) to a user.</summary>
    /// <param name="userId">User id.</param>
    /// <returns>True when available.</returns>
    public bool IsAvailable(Guid userId)
        => !Ids(Plugin.Config.DisabledUserIds).Contains(userId.ToString("N"), OIC);

    /// <summary>Gets whether kids mode is currently active for a user.</summary>
    /// <param name="userId">User id.</param>
    /// <returns>True when active.</returns>
    public bool IsActive(Guid userId)
        => Ids(Plugin.Config.ActiveUserIds).Contains(userId.ToString("N"), OIC);

    private static HashSet<Guid> ParseSet(IEnumerable<string> ids)
    {
        var set = new HashSet<Guid>();
        foreach (var s in ids)
        {
            if (Guid.TryParse(s, out var g))
            {
                set.Add(g);
            }
        }

        return set;
    }

    /// <summary>Gets the effective set of item ids visible in kids mode for a user.</summary>
    /// <param name="userId">User id.</param>
    /// <returns>The effective id set.</returns>
    public HashSet<Guid> EffectiveIds(Guid userId)
    {
        var set = ParseSet(Ids(Plugin.Config.KidsItemIds));
        var ov = Plugin.Config.GetOverride(userId);
        if (ov is not null)
        {
            foreach (var g in ParseSet(Ids(ov.AddIds)))
            {
                set.Add(g);
            }

            foreach (var g in ParseSet(Ids(ov.RemoveIds)))
            {
                set.Remove(g);
            }
        }

        return set;
    }

    // ---------------------------------------------------------------- state

    /// <summary>Gets the kids state for a user.</summary>
    /// <param name="user">The user.</param>
    /// <returns>The state.</returns>
    public KidsState GetState(User user)
    {
        var policy = GetPolicy(user);
        return new KidsState
        {
            Enabled = IsAvailable(user.Id),
            Active = IsActive(user.Id),
            IsAdministrator = policy.IsAdministrator,
            KidsTag = MarkerTag
        };
    }

    /// <summary>Gets rows for every user for the admin page.</summary>
    /// <returns>User rows.</returns>
    public IReadOnlyList<KidsUserInfo> GetUsers()
        => _userManager.GetUsers().Select(u =>
        {
            var ov = Plugin.Config.GetOverride(u.Id);
            return new KidsUserInfo
            {
                Id = u.Id.ToString("N"),
                Username = u.Username,
                IsAdministrator = GetPolicy(u).IsAdministrator,
                Enabled = IsAvailable(u.Id),
                Active = IsActive(u.Id),
                EffectiveCount = EffectiveIds(u.Id).Count,
                Added = ov is null ? 0 : Ids(ov.AddIds).Length,
                Removed = ov is null ? 0 : Ids(ov.RemoveIds).Length
            };
        }).OrderByDescending(r => r.IsAdministrator).ThenBy(r => r.Username, OIC).ToArray();

    // ---------------------------------------------------------------- toggle active

    /// <summary>Activates or deactivates kids mode for a user.</summary>
    /// <param name="userId">User id.</param>
    /// <param name="active">Whether kids mode should be active.</param>
    /// <returns>The refreshed state, or <c>null</c> if the user is unknown.</returns>
    public async Task<KidsState?> SetActiveAsync(Guid userId, bool active)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return null;
        }

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (active && !IsAvailable(userId))
            {
                return GetState(user);
            }

            var activeList = new HashSet<string>(Ids(Plugin.Config.ActiveUserIds), OIC);
            var key = userId.ToString("N");
            var policy = GetPolicy(user);

            if (active)
            {
                if (!activeList.Contains(key))
                {
                    StashPolicy(userId, policy);
                    activeList.Add(key);
                }

                await ReconcileUserTagsAsync(userId).ConfigureAwait(false);
                ApplyKidsPolicy(userId, policy);
            }
            else
            {
                activeList.Remove(key);
                RestorePolicy(userId, policy);
            }

            Plugin.Config.ActiveUserIds = activeList.OrderBy(s => s).ToArray();
            Plugin.Instance?.Save();
            await _userManager.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);

            var s = GetState(user);
            s.Active = active;
            return s;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void StashPolicy(Guid userId, UserPolicy policy)
    {
        var list = (Plugin.Config.SavedPolicies ?? [])
            .Where(p => !string.Equals(p.UserId, userId.ToString("N"), StringComparison.OrdinalIgnoreCase))
            .ToList();
        list.Add(new KidsSavedPolicy
        {
            UserId = userId.ToString("N"),
            AllowedTags = policy.AllowedTags ?? [],
            EnableAllFolders = policy.EnableAllFolders,
            EnabledFolders = (policy.EnabledFolders ?? []).Select(g => g.ToString("N")).ToArray(),
            EnableLiveTvAccess = policy.EnableLiveTvAccess
        });
        Plugin.Config.SavedPolicies = list.ToArray();
    }

    private void ApplyKidsPolicy(Guid userId, UserPolicy policy)
    {
        var tag = UserTag(userId);
        policy.AllowedTags = new[] { tag };

        var folders = new HashSet<Guid>();
        foreach (var id in EffectiveIds(userId))
        {
            var item = _libraryManager.GetItemById(id);
            if (item is null)
            {
                continue;
            }

            foreach (var cf in _libraryManager.GetCollectionFolders(item))
            {
                folders.Add(cf.Id);
            }
        }

        policy.EnableAllFolders = false;
        policy.EnabledFolders = folders.ToArray();
        policy.EnableLiveTvAccess = false;
    }

    private void RestorePolicy(Guid userId, UserPolicy policy)
    {
        var saved = (Plugin.Config.SavedPolicies ?? [])
            .FirstOrDefault(p => string.Equals(p.UserId, userId.ToString("N"), StringComparison.OrdinalIgnoreCase));

        if (saved is not null)
        {
            policy.AllowedTags = saved.AllowedTags ?? [];
            policy.EnableAllFolders = saved.EnableAllFolders;
            policy.EnabledFolders = ParseSet(Ids(saved.EnabledFolders)).ToArray();
            policy.EnableLiveTvAccess = saved.EnableLiveTvAccess;

            Plugin.Config.SavedPolicies = (Plugin.Config.SavedPolicies ?? [])
                .Where(p => !string.Equals(p.UserId, userId.ToString("N"), StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        else
        {
            policy.AllowedTags = [];
            policy.EnableAllFolders = true;
            policy.EnabledFolders = [];
            policy.EnableLiveTvAccess = true;
        }
    }

    // ---------------------------------------------------------------- item membership

    /// <summary>Gets whether an item is in the kids list relevant to the caller.</summary>
    /// <param name="userId">Caller id.</param>
    /// <param name="isAdmin">Whether the caller is an administrator.</param>
    /// <param name="itemId">Item id.</param>
    /// <returns>The item state, or <c>null</c> when the item is unknown.</returns>
    public KidsItemState? GetItemState(Guid userId, bool isAdmin, Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        var st = ToRow(item);
        st.InKids = isAdmin
            ? ParseSet(Ids(Plugin.Config.KidsItemIds)).Contains(itemId)
            : EffectiveIds(userId).Contains(itemId);
        return st;
    }

    /// <summary>Admin: toggles an item in the global kids list.</summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="inKids">Target state.</param>
    /// <returns>The item state, or <c>null</c> when unknown.</returns>
    public async Task<KidsItemState?> SetAdminItemAsync(Guid itemId, bool inKids)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var set = new HashSet<string>(Ids(Plugin.Config.KidsItemIds), OIC);
            var key = itemId.ToString("N");
            if (inKids)
            {
                set.Add(key);
            }
            else
            {
                set.Remove(key);
            }

            Plugin.Config.KidsItemIds = set.OrderBy(s => s).ToArray();
            Plugin.Instance?.Save();
            await ApplyTagAsync(item, MarkerTag, inKids).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        await ReconcileAllAsync().ConfigureAwait(false);
        return GetItemState(Guid.Empty, true, itemId);
    }

    /// <summary>User: toggles an item in their personal override.</summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <param name="inKids">Target state.</param>
    /// <returns>The item state, or <c>null</c> when unknown.</returns>
    public async Task<KidsItemState?> SetUserItemAsync(Guid userId, Guid itemId, bool inKids)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var inAdmin = ParseSet(Ids(Plugin.Config.KidsItemIds)).Contains(itemId);
            var list = (Plugin.Config.Overrides ?? []).ToList();
            var ov = list.FirstOrDefault(o => string.Equals(o.UserId, userId.ToString("N"), StringComparison.OrdinalIgnoreCase));
            if (ov is null)
            {
                ov = new KidsUserOverride { UserId = userId.ToString("N") };
                list.Add(ov);
            }

            var add = new HashSet<string>(Ids(ov.AddIds), OIC);
            var rem = new HashSet<string>(Ids(ov.RemoveIds), OIC);
            var key = itemId.ToString("N");
            add.Remove(key);
            rem.Remove(key);

            if (inKids && !inAdmin)
            {
                add.Add(key);
            }
            else if (!inKids && inAdmin)
            {
                rem.Add(key);
            }

            ov.AddIds = add.OrderBy(s => s).ToArray();
            ov.RemoveIds = rem.OrderBy(s => s).ToArray();

            // drop empty overrides
            list.RemoveAll(o => Ids(o.AddIds).Length == 0 && Ids(o.RemoveIds).Length == 0);
            Plugin.Config.Overrides = list.ToArray();
            Plugin.Instance?.Save();
        }
        finally
        {
            _lock.Release();
        }

        await ReconcileUserAsync(userId).ConfigureAwait(false);
        return GetItemState(userId, false, itemId);
    }

    // ---------------------------------------------------------------- availability

    /// <summary>Admin: enables or disables kids mode availability for a user.</summary>
    /// <param name="userId">User id.</param>
    /// <param name="enabled">Whether kids mode is offered.</param>
    /// <returns>A task.</returns>
    public async Task SetAvailableAsync(Guid userId, bool enabled)
    {
        if (!enabled && IsActive(userId))
        {
            await SetActiveAsync(userId, false).ConfigureAwait(false);
        }

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var set = new HashSet<string>(Ids(Plugin.Config.DisabledUserIds), OIC);
            var key = userId.ToString("N");
            if (enabled)
            {
                set.Remove(key);
            }
            else
            {
                set.Add(key);
            }

            Plugin.Config.DisabledUserIds = set.OrderBy(s => s).ToArray();
            Plugin.Instance?.Save();
        }
        finally
        {
            _lock.Release();
        }

        if (enabled)
        {
            await ReconcileUserAsync(userId).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- lists for the config page

    /// <summary>Gets the admin global kids list resolved against the library.</summary>
    /// <returns>Item rows.</returns>
    public IReadOnlyList<KidsItemState> GetAdminItems()
        => ResolveRows(Ids(Plugin.Config.KidsItemIds), "admin", inKids: true);

    /// <summary>Gets a user's effective kids list for the admin page.</summary>
    /// <param name="userId">User id.</param>
    /// <returns>Item rows.</returns>
    public IReadOnlyList<KidsItemState> GetUserItems(Guid userId)
    {
        var admin = ParseSet(Ids(Plugin.Config.KidsItemIds));
        var ov = Plugin.Config.GetOverride(userId);
        var add = ov is null ? new HashSet<Guid>() : ParseSet(Ids(ov.AddIds));
        var rem = ov is null ? new HashSet<Guid>() : ParseSet(Ids(ov.RemoveIds));
        var effective = EffectiveIds(userId);

        var union = new HashSet<Guid>(admin);
        union.UnionWith(add);

        var rows = new List<KidsItemState>();
        foreach (var id in union)
        {
            var item = _libraryManager.GetItemById(id);
            KidsItemState row = item is null
                ? new KidsItemState { Id = id.ToString("N"), Name = "(elemento eliminado)", Exists = false }
                : ToRow(item);
            row.InKids = effective.Contains(id);
            row.Source = add.Contains(id) ? "added" : rem.Contains(id) ? "removed" : "admin";
            rows.Add(row);
        }

        return rows.OrderByDescending(r => r.Exists).ThenBy(r => r.Name, OIC).ToArray();
    }

    private IReadOnlyList<KidsItemState> ResolveRows(IEnumerable<string> ids, string source, bool inKids)
    {
        var rows = new List<KidsItemState>();
        foreach (var s in ids)
        {
            if (!Guid.TryParse(s, out var g))
            {
                continue;
            }

            var item = _libraryManager.GetItemById(g);
            KidsItemState row = item is null
                ? new KidsItemState { Id = g.ToString("N"), Name = "(elemento eliminado)", Exists = false }
                : ToRow(item);
            row.Source = source;
            row.InKids = inKids && row.Exists;
            rows.Add(row);
        }

        return rows.OrderByDescending(r => r.Exists).ThenBy(r => r.Name, OIC).ToArray();
    }

    // ---------------------------------------------------------------- reconcile

    /// <summary>Reconciles every available user's personal tags and (if active) their policy.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of tag writes.</returns>
    public async Task<int> ReconcileAllAsync(CancellationToken cancellationToken = default)
    {
        // keep the marker tag on the admin list
        foreach (var s in Ids(Plugin.Config.KidsItemIds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Guid.TryParse(s, out var g) && _libraryManager.GetItemById(g) is { } it && !HasTag(it, MarkerTag))
            {
                await ApplyTagAsync(it, MarkerTag, true).ConfigureAwait(false);
            }
        }

        var writes = 0;
        foreach (var user in _userManager.GetUsers())
        {
            if (!IsAvailable(user.Id))
            {
                continue;
            }

            writes += await ReconcileUserTagsAsync(user.Id, cancellationToken).ConfigureAwait(false);
            if (IsActive(user.Id))
            {
                var policy = GetPolicy(user);
                ApplyKidsPolicy(user.Id, policy);
                await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
            }
        }

        return writes;
    }

    /// <summary>Reconciles one user's personal tags and (if active) their policy.</summary>
    /// <param name="userId">User id.</param>
    /// <returns>Number of tag writes.</returns>
    public async Task<int> ReconcileUserAsync(Guid userId)
    {
        var writes = await ReconcileUserTagsAsync(userId).ConfigureAwait(false);
        if (IsActive(userId) && _userManager.GetUserById(userId) is { } user)
        {
            var policy = GetPolicy(user);
            ApplyKidsPolicy(userId, policy);
            await _userManager.UpdatePolicyAsync(userId, policy).ConfigureAwait(false);
        }

        return writes;
    }

    private async Task<int> ReconcileUserTagsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var tag = UserTag(userId);
        var want = EffectiveIds(userId);
        var writes = 0;

        // items that currently carry the personal tag
        List<BaseItem> tagged;
        try
        {
            tagged = _libraryManager
                .GetItemList(new InternalItemsQuery { Recursive = true, IncludeItemTypes = _kinds, Tags = new[] { tag } })
                .Where(i => HasTag(i, tag))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kids Mode: tag query failed for {Tag}.", tag);
            tagged = new List<BaseItem>();
        }

        foreach (var item in tagged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!want.Contains(item.Id))
            {
                await ApplyTagAsync(item, tag, false).ConfigureAwait(false);
                writes++;
            }
        }

        var have = tagged.Select(i => i.Id).ToHashSet();
        foreach (var id in want)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (have.Contains(id))
            {
                continue;
            }

            var item = _libraryManager.GetItemById(id);
            if (item is not null && !HasTag(item, tag))
            {
                await ApplyTagAsync(item, tag, true).ConfigureAwait(false);
                writes++;
            }
        }

        return writes;
    }

    /// <summary>Startup consistency: reconcile tags and re-assert active users' policies.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task EnsureConsistentAsync(CancellationToken cancellationToken = default)
    {
        // drop active flags for users that are no longer available
        var active = new HashSet<string>(Ids(Plugin.Config.ActiveUserIds), OIC);
        foreach (var raw in active.ToArray())
        {
            if (Guid.TryParse(raw, out var uid) && !IsAvailable(uid))
            {
                await SetActiveAsync(uid, false).ConfigureAwait(false);
            }
        }

        await ReconcileAllAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reconciles the admin list with the library and rebuilds every user's tags.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary.</returns>
    public async Task<KidsSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var result = new KidsSyncResult();
        try
        {
            var known = ParseSet(Ids(Plugin.Config.KidsItemIds));

            IReadOnlyList<BaseItem> all;
            try
            {
                all = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = _kinds,
                    Tags = new[] { MarkerTag }
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kids Mode: marker query failed.");
                all = Array.Empty<BaseItem>();
            }

            foreach (var item in all)
            {
                if (HasTag(item, MarkerTag) && known.Add(item.Id))
                {
                    result.Discovered++;
                }
            }

            try
            {
                foreach (var folder in _libraryManager.RootFolder.Children)
                {
                    if (HasTag(folder, MarkerTag) && known.Add(folder.Id))
                    {
                        result.Discovered++;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Kids Mode: root folder enumeration failed.");
            }

            var final = new List<Guid>();
            foreach (var id in known)
            {
                if (_libraryManager.GetItemById(id) is null)
                {
                    result.Removed++;
                    continue;
                }

                final.Add(id);
            }

            Plugin.Config.KidsItemIds = final.Select(g => g.ToString("N")).OrderBy(s => s).ToArray();
            Plugin.Instance?.Save();
            result.AdminTotal = final.Count;
        }
        finally
        {
            _lock.Release();
        }

        foreach (var user in _userManager.GetUsers())
        {
            if (!IsAvailable(user.Id))
            {
                continue;
            }

            result.TagWrites += await ReconcileUserAsync(user.Id).ConfigureAwait(false);
            result.UsersReconciled++;
        }

        _logger.LogInformation(
            "Kids Mode sync: admin {Admin}, discovered {Disc}, removed {Rem}, users {Users}, writes {Writes}.",
            result.AdminTotal, result.Discovered, result.Removed, result.UsersReconciled, result.TagWrites);
        return result;
    }

    // ---------------------------------------------------------------- helpers

    private static bool HasTag(BaseItem item, string tag)
        => (item.Tags ?? []).Contains(tag, StringComparer.OrdinalIgnoreCase);

    private async Task ApplyTagAsync(BaseItem item, string tag, bool present)
    {
        var tags = new HashSet<string>(item.Tags ?? [], StringComparer.OrdinalIgnoreCase);
        var changed = present ? tags.Add(tag) : tags.Remove(tag);
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

    private static KidsItemState ToRow(BaseItem item) => new()
    {
        Id = item.Id.ToString("N"),
        Name = item.Name ?? item.Id.ToString("N"),
        Type = item.GetType().Name,
        Path = item.Path ?? string.Empty,
        Exists = true
    };

    private UserPolicy GetPolicy(User user)
    {
        var dto = _userManager.GetUserDto(user, string.Empty);
        return dto.Policy ?? new UserPolicy();
    }
}

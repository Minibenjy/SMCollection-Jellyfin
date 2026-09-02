using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.KidsMode.Configuration;

/// <summary>
/// Configuration for the Kids Mode plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the topbar switch script is injected.
    /// </summary>
    public bool InjectClientScript { get; set; } = true;

    /// <summary>
    /// Gets or sets the human-visible marker tag applied to admin-curated kids items.
    /// </summary>
    public string KidsTag { get; set; } = "kids";

    /// <summary>
    /// Gets or sets the admin-curated global allow-list of item ids (GUID "N").
    /// </summary>
    public string[] KidsItemIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the ids of users that do NOT get kids mode (default: every user has it).
    /// </summary>
    public string[] DisabledUserIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the ids of users that are currently inside kids mode.
    /// </summary>
    public string[] ActiveUserIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the per-user allow-list overrides.
    /// </summary>
    public KidsUserOverride[] Overrides { get; set; } = [];

    /// <summary>
    /// Gets or sets the stashed pre-kids policy fields, so they can be restored on exit.
    /// </summary>
    public KidsSavedPolicy[] SavedPolicies { get; set; } = [];

    /// <summary>
    /// Gets the override row for a user, or <c>null</c>.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The override, or <c>null</c>.</returns>
    public KidsUserOverride? GetOverride(Guid userId)
    {
        var n = userId.ToString("N");
        foreach (var o in Overrides)
        {
            if (string.Equals(o.UserId, n, StringComparison.OrdinalIgnoreCase))
            {
                return o;
            }
        }

        return null;
    }
}

/// <summary>
/// Per-user additions/removals relative to the admin kids list.
/// </summary>
public class KidsUserOverride
{
    /// <summary>Gets or sets the user id (GUID "N").</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets ids the user added to their kids view.</summary>
    public string[] AddIds { get; set; } = [];

    /// <summary>Gets or sets ids the user removed from their kids view.</summary>
    public string[] RemoveIds { get; set; } = [];
}

/// <summary>
/// Snapshot of the policy fields kids mode overwrites, taken when a user enters kids mode.
/// </summary>
public class KidsSavedPolicy
{
    /// <summary>Gets or sets the user id (GUID "N").</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the saved allowed tags.</summary>
    public string[] AllowedTags { get; set; } = [];

    /// <summary>Gets or sets the saved EnableAllFolders flag.</summary>
    public bool EnableAllFolders { get; set; } = true;

    /// <summary>Gets or sets the saved enabled folder ids.</summary>
    public string[] EnabledFolders { get; set; } = [];

    /// <summary>Gets or sets the saved live tv access flag.</summary>
    public bool EnableLiveTvAccess { get; set; } = true;
}

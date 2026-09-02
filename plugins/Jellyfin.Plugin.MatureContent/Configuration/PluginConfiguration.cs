using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MatureContent.Configuration;

/// <summary>
/// Configuration for the Mature Content plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the topbar toggle script is injected into the web UI.
    /// </summary>
    public bool InjectClientScript { get; set; } = true;

    /// <summary>
    /// Gets or sets the tags that identify mature content.
    /// </summary>
    public string[] MatureTags { get; set; } = ["mature", "+18"];

    /// <summary>
    /// Gets or sets a value indicating whether admins see the topbar toggle unless overridden.
    /// </summary>
    public bool ShowToggleForAdministrators { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether non-admin users see the topbar toggle unless overridden.
    /// </summary>
    public bool ShowToggleForUsers { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether users without a toggle can see mature content.
    /// </summary>
    public bool DefaultVisibleWhenToggleHidden { get; set; }

    /// <summary>
    /// Gets or sets per-user overrides.
    /// </summary>
    public UserMatureRule[] UserRules { get; set; } = [];

    /// <summary>
    /// Gets or sets the durable list of item ids that have been marked as mature.
    /// This is the authoritative history and is re-applied to the library on startup
    /// and after every library scan, so a metadata refresh cannot silently drop the tag.
    /// </summary>
    public string[] MarkedItemIds { get; set; } = [];

    /// <summary>
    /// Gets the matching rule for a user.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <returns>The matching rule, or <c>null</c>.</returns>
    public UserMatureRule? GetRule(Guid userId)
    {
        var normalized = userId.ToString("N");
        foreach (var rule in UserRules)
        {
            if (string.Equals(rule.UserId, normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(rule.UserId, userId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                return rule;
            }
        }

        return null;
    }
}

/// <summary>
/// Per-user mature content behavior.
/// </summary>
public class UserMatureRule
{
    /// <summary>
    /// Gets or sets the user identifier.
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this user sees the topbar toggle.
    /// </summary>
    public bool ShowToggle { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether mature content is visible when no toggle is shown.
    /// </summary>
    public bool DefaultVisibleWhenToggleHidden { get; set; }
}

namespace Jellyfin.Plugin.MatureContent.Models;

/// <summary>
/// State returned to the web client for the current user.
/// </summary>
public class MatureState
{
    /// <summary>
    /// Gets or sets a value indicating whether the user can switch mature content.
    /// </summary>
    public bool CanToggle { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether mature content is currently visible.
    /// </summary>
    public bool MatureVisible { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the current visibility was forced by configuration.
    /// </summary>
    public bool Locked { get; set; }

    /// <summary>
    /// Gets or sets the configured mature tags.
    /// </summary>
    public string[] MatureTags { get; set; } = [];
}

/// <summary>
/// Request to update mature visibility for the current user.
/// </summary>
public class UpdateMatureStateRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether mature content should be visible.
    /// </summary>
    public bool MatureVisible { get; set; }
}

/// <summary>
/// User row returned to the configuration page.
/// </summary>
public class MatureUserInfo
{
    /// <summary>
    /// Gets or sets the user identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the username.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the user is an administrator.
    /// </summary>
    public bool IsAdministrator { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether mature content is currently visible.
    /// </summary>
    public bool MatureVisible { get; set; }
}

/// <summary>
/// Request to update mature tags on an item.
/// </summary>
public class UpdateItemMatureRequest
{
    /// <summary>
    /// Gets or sets a value indicating whether the item should be marked as mature.
    /// </summary>
    public bool IsMature { get; set; }
}

/// <summary>
/// Item mature tagging result.
/// </summary>
public class MatureItemState
{
    /// <summary>
    /// Gets or sets the item identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the item has a mature tag.
    /// </summary>
    public bool IsMature { get; set; }

    /// <summary>
    /// Gets or sets current tags.
    /// </summary>
    public string[] Tags { get; set; } = [];

    /// <summary>
    /// Gets or sets the item type (Series, Movie, Book, CollectionFolder...).
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item path, when available.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the item still exists in the library.
    /// </summary>
    public bool Exists { get; set; } = true;
}

/// <summary>
/// Result of a reconcile/sync run.
/// </summary>
public class MatureSyncResult
{
    /// <summary>
    /// Gets or sets the number of items that carry a mature tag after the sync.
    /// </summary>
    public int Total { get; set; }

    /// <summary>
    /// Gets or sets the number of items discovered in the library that were not in the saved history.
    /// </summary>
    public int Discovered { get; set; }

    /// <summary>
    /// Gets or sets the number of items from the saved history whose tag was missing and got re-applied.
    /// </summary>
    public int Reapplied { get; set; }

    /// <summary>
    /// Gets or sets the number of saved ids that no longer resolve to a library item.
    /// </summary>
    public int Removed { get; set; }
}

namespace Jellyfin.Plugin.KidsMode.Models;

/// <summary>State returned to the web client for the current user.</summary>
public class KidsState
{
    /// <summary>Gets or sets a value indicating whether kids mode is available for this user.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether kids mode is currently active for this user.</summary>
    public bool Active { get; set; }

    /// <summary>Gets or sets a value indicating whether the current user manages the global list (admin) vs their own override.</summary>
    public bool IsAdministrator { get; set; }

    /// <summary>Gets or sets the marker tag.</summary>
    public string KidsTag { get; set; } = "kids";
}

/// <summary>Request to change the current user's kids-active state.</summary>
public class UpdateKidsStateRequest
{
    /// <summary>Gets or sets a value indicating whether kids mode should be active.</summary>
    public bool Active { get; set; }
}

/// <summary>Request to toggle a single item in a kids list.</summary>
public class UpdateKidsItemRequest
{
    /// <summary>Gets or sets a value indicating whether the item should be in the kids list.</summary>
    public bool InKids { get; set; }
}

/// <summary>Request to change a per-user flag.</summary>
public class UpdateKidsFlagRequest
{
    /// <summary>Gets or sets the flag value.</summary>
    public bool Value { get; set; }
}

/// <summary>User row for the admin config page.</summary>
public class KidsUserInfo
{
    /// <summary>Gets or sets the user id (GUID "N").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the username.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the user is an administrator.</summary>
    public bool IsAdministrator { get; set; }

    /// <summary>Gets or sets a value indicating whether kids mode is available for the user.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether kids mode is currently active for the user.</summary>
    public bool Active { get; set; }

    /// <summary>Gets or sets how many items are in the user's effective kids list.</summary>
    public int EffectiveCount { get; set; }

    /// <summary>Gets or sets how many personal additions the user has.</summary>
    public int Added { get; set; }

    /// <summary>Gets or sets how many admin items the user has removed.</summary>
    public int Removed { get; set; }
}

/// <summary>Item row for kids lists.</summary>
public class KidsItemState
{
    /// <summary>Gets or sets the item id (GUID "N").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the item name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the item type.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the item path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the item still exists.</summary>
    public bool Exists { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the item is in the relevant kids list.</summary>
    public bool InKids { get; set; }

    /// <summary>Gets or sets the source: "admin", "added" or "removed" (per-user context only).</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>Result of a sync run.</summary>
public class KidsSyncResult
{
    /// <summary>Gets or sets the admin list size after sync.</summary>
    public int AdminTotal { get; set; }

    /// <summary>Gets or sets items discovered carrying the marker tag.</summary>
    public int Discovered { get; set; }

    /// <summary>Gets or sets dead ids removed.</summary>
    public int Removed { get; set; }

    /// <summary>Gets or sets the number of users whose personal tags were reconciled.</summary>
    public int UsersReconciled { get; set; }

    /// <summary>Gets or sets the number of tag writes performed during reconcile.</summary>
    public int TagWrites { get; set; }
}

using System;

namespace Jellyfin.Plugin.AiAssistant.Configuration;

/// <summary>
/// A single user's own assistant settings.
/// </summary>
/// <remarks>
/// Contains no secrets. The credential belonging to these settings lives in the
/// encrypted vault, keyed by the same user and provider.
/// </remarks>
public class UserPreferences
{
    /// <summary>Gets or sets the provider this user routes to.</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets the endpoint override, when the provider takes one.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the model to use.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets this user's override for the library's metadata language.
    /// Empty falls back to the server-wide setting.
    /// </summary>
    public string MetadataLanguage { get; set; } = string.Empty;
}

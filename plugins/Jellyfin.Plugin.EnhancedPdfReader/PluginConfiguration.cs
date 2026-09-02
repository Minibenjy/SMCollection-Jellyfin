using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.EnhancedPdfReader;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the client script is injected into the web UI.
    /// </summary>
    public bool InjectClientScript { get; set; } = true;

    /// <summary>
    /// Gets or sets the default zoom mode ("fit-width", "fit-page", or a numeric percentage string).
    /// </summary>
    public string DefaultZoomMode { get; set; } = "fit-width";
}

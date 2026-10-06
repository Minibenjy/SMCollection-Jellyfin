using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ArtworkRefresher;

/// <summary>
/// The Artwork Refresher plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Artwork Refresher";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("5ba2fb72-e545-4a44-89e1-a42b6e740208");

    /// <inheritdoc />
    public override string Description => "Refreshes posters, backdrops, logos and category images from public sources on a schedule, or for one item from its card menu.";

    /// <summary>
    /// Gets the current configuration, falling back to defaults before the plugin is loaded.
    /// </summary>
    public static PluginConfiguration Config => Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Persists the current configuration to disk.
    /// </summary>
    public void Save() => SaveConfiguration();

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace)
        };
    }
}

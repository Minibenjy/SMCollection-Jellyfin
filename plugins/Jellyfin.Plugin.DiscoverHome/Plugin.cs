using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.DiscoverHome.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DiscoverHome;

/// <summary>
/// The DiscoverHome plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>Gets the effective configuration.</summary>
    public static PluginConfiguration Config => Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public override string Name => "Discover Home";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("c3f7a1d8-5b42-4e69-9a0c-7d1e8f3b2a56");

    /// <inheritdoc />
    public override string Description =>
        "A discovery-style home screen: pinned sidebar, centered search, compact cards and genre/studio carousels interleaved with Jellyfin's own rows.";

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

using Jellyfin.Plugin.DiscoverHome.Artwork;
using Jellyfin.Plugin.DiscoverHome.Injection;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DiscoverHome;

/// <summary>
/// Registers plugin services with the host container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<StudioArtworkCache>();
        serviceCollection.AddHostedService<ScriptInjectionHostedService>();
        serviceCollection.AddHostedService<FileTransformationFallbackHostedService>();
    }
}

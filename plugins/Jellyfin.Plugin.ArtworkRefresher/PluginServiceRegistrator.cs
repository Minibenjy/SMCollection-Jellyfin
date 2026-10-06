using Jellyfin.Plugin.ArtworkRefresher.Injection;
using Jellyfin.Plugin.ArtworkRefresher.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ArtworkRefresher;

/// <summary>
/// Registers the plugin's services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Singletons, so the scheduled task, the page and the card menu share one run, one log and one state.
        serviceCollection.AddSingleton<ArtworkRefreshService>();
        serviceCollection.AddSingleton<OptionalIntegrationDetector>();
        serviceCollection.AddSingleton<HomeArtworkService>();
        serviceCollection.AddHostedService<NewItemsHostedService>();
        serviceCollection.AddHostedService<ScriptInjectionHostedService>();
        serviceCollection.AddHostedService<FileTransformationFallbackHostedService>();
    }
}

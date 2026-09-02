using Jellyfin.Plugin.AutoThumbnails.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AutoThumbnails;

/// <summary>
/// Registers the plugin's services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // A singleton, so the dashboard page and the scheduled task share one run and one log.
        serviceCollection.AddSingleton<ThumbnailJobService>();
    }
}

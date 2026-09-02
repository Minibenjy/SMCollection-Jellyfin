using Jellyfin.Plugin.KidsMode.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.KidsMode.Injection;

/// <summary>
/// Registers plugin services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<KidsPolicyService>();
        serviceCollection.AddHostedService<KidsStartupHostedService>();
        serviceCollection.AddHostedService<ScriptInjectionHostedService>();
    }
}

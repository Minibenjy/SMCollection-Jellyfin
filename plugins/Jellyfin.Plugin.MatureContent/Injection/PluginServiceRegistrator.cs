using Jellyfin.Plugin.MatureContent.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MatureContent.Injection;

/// <summary>
/// Registers plugin services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<MaturePolicyService>();
        serviceCollection.AddHostedService<MatureStartupHostedService>();
        serviceCollection.AddHostedService<ScriptInjectionHostedService>();
    }
}

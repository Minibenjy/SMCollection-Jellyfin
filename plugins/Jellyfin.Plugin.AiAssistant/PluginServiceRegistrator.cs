using Jellyfin.Plugin.AiAssistant.Assistant;
using Jellyfin.Plugin.AiAssistant.Configuration;
using Jellyfin.Plugin.AiAssistant.Guardrails;
using Jellyfin.Plugin.AiAssistant.Injection;
using Jellyfin.Plugin.AiAssistant.Providers;
using Jellyfin.Plugin.AiAssistant.Providers.Ollama;
using Jellyfin.Plugin.AiAssistant.Security;
using Jellyfin.Plugin.AiAssistant.Tools;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AiAssistant;

/// <summary>
/// Registers plugin services with the host container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ICredentialStore, EncryptedCredentialStore>();
        serviceCollection.AddSingleton<IUserPreferenceStore, UserPreferenceStore>();
        serviceCollection.AddSingleton<MetadataLanguageResolver>();

        // Each provider is registered against the shared interface, so nothing above
        // this line knows which vendors exist.
        serviceCollection.AddSingleton<IChatProvider, OllamaProvider>();
        serviceCollection.AddSingleton<ProviderResolver>();

        // Every tool registered here becomes part of the assistant's capability surface.
        // Reading.
        serviceCollection.AddSingleton<IAssistantTool, ListLibrariesTool>();
        serviceCollection.AddSingleton<IAssistantTool, SearchLibraryTool>();
        serviceCollection.AddSingleton<IAssistantTool, GetItemDetailsTool>();
        serviceCollection.AddSingleton<IAssistantTool, ListEpisodesTool>();
        serviceCollection.AddSingleton<IAssistantTool, PickEpisodesTool>();
        serviceCollection.AddSingleton<IAssistantTool, ContinueWatchingTool>();
        serviceCollection.AddSingleton<IAssistantTool, ListPlaylistsTool>();
        serviceCollection.AddSingleton<IAssistantTool, GetPlaylistTool>();

        // Writing. Every one of these is confirmed with the user before it runs.
        serviceCollection.AddSingleton<IAssistantTool, CreatePlaylistTool>();
        serviceCollection.AddSingleton<IAssistantTool, AddToPlaylistTool>();
        serviceCollection.AddSingleton<IAssistantTool, RemoveFromPlaylistTool>();
        serviceCollection.AddSingleton<IAssistantTool, ManagePlaylistTool>();
        serviceCollection.AddSingleton<IAssistantTool, SetWatchedTool>();
        serviceCollection.AddSingleton<IAssistantTool, SetFavoriteTool>();
        serviceCollection.AddSingleton<IAssistantTool, CreateCollectionTool>();
        serviceCollection.AddSingleton<ToolRegistry>();

        serviceCollection.AddSingleton<RateLimiter>();
        serviceCollection.AddSingleton<ConversationStore>();
        serviceCollection.AddSingleton<ConversationService>();

        serviceCollection.AddHostedService<ScriptInjectionHostedService>();
    }
}

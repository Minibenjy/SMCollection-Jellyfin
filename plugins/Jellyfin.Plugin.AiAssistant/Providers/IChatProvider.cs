using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AiAssistant.Providers;

/// <summary>
/// A backend that can run a tool-calling conversation.
/// </summary>
/// <remarks>
/// This is the plugin's only coupling point to any specific AI vendor. Everything
/// above this interface — tools, guardrails, permissions, the agent loop — is
/// provider-neutral. Adding a vendor means adding one implementation here and
/// nothing else.
/// </remarks>
public interface IChatProvider
{
    /// <summary>Gets the stable identifier used in configuration, e.g. "anthropic".</summary>
    string Id { get; }

    /// <summary>Gets the human-readable name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>Gets a value indicating whether this provider needs a credential.</summary>
    /// <remarks>Self-hosted backends such as Ollama typically do not.</remarks>
    bool RequiresCredential { get; }

    /// <summary>Gets a value indicating whether this provider can execute tool calls.</summary>
    /// <remarks>
    /// Small local models often cannot. The assistant degrades to answering from
    /// conversation context only, rather than silently producing wrong answers.
    /// </remarks>
    bool SupportsTools { get; }

    /// <summary>
    /// Lists the models available for the given endpoint and credential.
    /// </summary>
    /// <param name="connection">Resolved endpoint and credential.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Model identifiers.</returns>
    Task<IReadOnlyList<string>> ListModelsAsync(ProviderConnection connection, CancellationToken cancellationToken);

    /// <summary>
    /// Runs one completion turn.
    /// </summary>
    /// <param name="connection">Resolved endpoint and credential.</param>
    /// <param name="request">The provider-neutral request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The provider-neutral response.</returns>
    Task<ChatResponse> CompleteAsync(ProviderConnection connection, ChatRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Everything a provider needs to reach its backend for one specific user.
/// </summary>
/// <param name="BaseUrl">Endpoint override, or null for the provider default.</param>
/// <param name="ApiKey">Decrypted credential, or null when the provider needs none.</param>
public record ProviderConnection(string? BaseUrl, string? ApiKey);

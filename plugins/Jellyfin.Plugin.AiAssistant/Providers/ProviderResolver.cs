using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiAssistant.Configuration;
using Jellyfin.Plugin.AiAssistant.Security;

namespace Jellyfin.Plugin.AiAssistant.Providers;

/// <summary>
/// Decides which provider, endpoint, model and credential a given user runs with.
/// </summary>
/// <remarks>
/// Every administrator policy is enforced here, on the server, and never in the
/// browser: whether users may bring their own provider, which providers are
/// permitted, what the fallback is, and whether the server's own credential may be
/// borrowed. A crafted API request cannot route around these checks.
/// </remarks>
public sealed class ProviderResolver
{
    /// <summary>Vault key under which the server-wide credential is stored.</summary>
    public static readonly Guid ServerCredentialOwner = Guid.Empty;

    private readonly IReadOnlyDictionary<string, IChatProvider> _providers;
    private readonly IUserPreferenceStore _preferences;
    private readonly ICredentialStore _credentials;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderResolver"/> class.
    /// </summary>
    /// <param name="providers">All registered providers.</param>
    /// <param name="preferences">Per-user settings.</param>
    /// <param name="credentials">Credential vault.</param>
    public ProviderResolver(
        IEnumerable<IChatProvider> providers,
        IUserPreferenceStore preferences,
        ICredentialStore credentials)
    {
        _providers = providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        _preferences = preferences;
        _credentials = credentials;
    }

    /// <summary>
    /// Gets the providers a user is permitted to choose from.
    /// </summary>
    /// <returns>The permitted providers.</returns>
    public IReadOnlyList<IChatProvider> GetSelectableProviders()
    {
        var config = Plugin.Config;
        if (!config.AllowUserProviders)
        {
            return Array.Empty<IChatProvider>();
        }

        return _providers.Values.Where(p => IsPermitted(p.Id)).ToList();
    }

    /// <summary>
    /// Determines whether the administrator permits a provider.
    /// </summary>
    /// <param name="providerId">Provider identifier.</param>
    /// <returns>Whether it is allowed.</returns>
    public bool IsPermitted(string providerId)
    {
        var allowed = Plugin.Config.AllowedProviders;
        if (string.IsNullOrWhiteSpace(allowed))
        {
            return _providers.ContainsKey(providerId);
        }

        return allowed
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(id => string.Equals(id, providerId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves the effective route for one user.
    /// </summary>
    /// <param name="userId">The acting user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved route.</returns>
    /// <exception cref="ProviderException">Thrown when no usable route exists.</exception>
    public async Task<ResolvedProvider> ResolveAsync(Guid userId, CancellationToken cancellationToken)
    {
        var route = await ResolveConnectionAsync(userId, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(route.Model))
        {
            throw new ProviderException("No model has been selected for the AI provider.");
        }

        return route;
    }

    /// <summary>
    /// Resolves the provider and endpoint without requiring a model to be chosen yet.
    /// </summary>
    /// <param name="userId">The acting user.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved route; its model may be empty.</returns>
    /// <remarks>
    /// Listing the models an endpoint offers has to work before a model is picked,
    /// otherwise a user cannot discover what to choose without already knowing.
    /// </remarks>
    public async Task<ResolvedProvider> ResolveConnectionAsync(Guid userId, CancellationToken cancellationToken)
    {
        var config = Plugin.Config;
        var own = config.AllowUserProviders
            ? await _preferences.GetAsync(userId, cancellationToken).ConfigureAwait(false)
            : null;

        // A user's stored choice is re-validated on every request, so revoking a
        // provider in the dashboard takes effect immediately rather than only for
        // users who have not configured one yet.
        var useOwn = own is not null
                     && !string.IsNullOrWhiteSpace(own.ProviderId)
                     && IsPermitted(own.ProviderId);

        var providerId = useOwn ? own!.ProviderId : config.DefaultProviderId;
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ProviderException(
                "No AI provider is configured. Set one up in your preferences, or ask the server administrator to configure a default.");
        }

        if (!_providers.TryGetValue(providerId, out var provider) || !IsPermitted(providerId))
        {
            throw new ProviderException("The configured AI provider is not available on this server.");
        }

        var baseUrl = useOwn ? own!.BaseUrl : config.DefaultBaseUrl;
        var model = useOwn ? own!.Model : config.DefaultModel;

        var apiKey = await ResolveCredentialAsync(userId, provider, useOwn, cancellationToken).ConfigureAwait(false);

        return new ResolvedProvider(
            provider,
            new ProviderConnection(NullIfBlank(baseUrl), apiKey),
            model ?? string.Empty);
    }

    private async Task<string?> ResolveCredentialAsync(
        Guid userId,
        IChatProvider provider,
        bool useOwn,
        CancellationToken cancellationToken)
    {
        if (!provider.RequiresCredential)
        {
            return null;
        }

        if (useOwn)
        {
            var personal = await _credentials.GetAsync(userId, provider.Id, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(personal))
            {
                return personal;
            }
        }

        // Falling back to the server's own key spends the administrator's money, so
        // it happens only when they have explicitly opted in.
        if (Plugin.Config.ShareServerCredential)
        {
            var shared = await _credentials
                .GetAsync(ServerCredentialOwner, provider.Id, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(shared))
            {
                return shared;
            }
        }

        throw new ProviderException(
            "This provider needs an API key. Add yours in your assistant preferences.");
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// A fully resolved route to an AI backend for one user.
/// </summary>
/// <param name="Provider">The provider implementation.</param>
/// <param name="Connection">Endpoint and credential.</param>
/// <param name="Model">The model to request.</param>
public record ResolvedProvider(IChatProvider Provider, ProviderConnection Connection, string Model);

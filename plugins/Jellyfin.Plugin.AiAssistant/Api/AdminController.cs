using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiAssistant.Providers;
using Jellyfin.Plugin.AiAssistant.Security;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AiAssistant.Api;

/// <summary>
/// Administrator endpoints for the server-wide credential.
/// </summary>
/// <remarks>
/// The server credential is handled here rather than through the plugin configuration
/// XML so that it is written to the encrypted vault. Even an administrator cannot
/// read it back through this API — only replace or remove it.
/// </remarks>
[ApiController]
[Route("AiAssistant/Admin")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class AdminController : ControllerBase
{
    private readonly ICredentialStore _credentials;
    private readonly IEnumerable<IChatProvider> _providers;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminController"/> class.
    /// </summary>
    /// <param name="credentials">Credential vault.</param>
    /// <param name="providers">All installed providers.</param>
    public AdminController(ICredentialStore credentials, IEnumerable<IChatProvider> providers)
    {
        _credentials = credentials;
        _providers = providers;
    }

    /// <summary>Lists every provider installed in this plugin.</summary>
    /// <returns>The installed providers.</returns>
    /// <remarks>
    /// Unlike the user-facing list, this is not filtered by the allow-list: the
    /// administrator needs to see everything in order to choose what to permit.
    /// </remarks>
    [HttpGet("/AiAssistant/Providers")]
    public ActionResult<IReadOnlyList<Dtos.ProviderInfoDto>> GetProviders()
        => Ok(_providers
            .Select(p => new Dtos.ProviderInfoDto(p.Id, p.DisplayName, p.RequiresCredential))
            .ToList());

    /// <summary>Gets a masked hint for the stored server credential.</summary>
    /// <param name="providerId">Provider the credential belongs to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The hint, or null when none is stored.</returns>
    [HttpGet("Credential/{providerId}")]
    public async Task<ActionResult<string?>> GetCredentialHint(
        string providerId,
        CancellationToken cancellationToken)
    {
        var hint = await _credentials
            .GetHintAsync(ProviderResolver.ServerCredentialOwner, providerId, cancellationToken)
            .ConfigureAwait(false);

        return Ok(hint);
    }

    /// <summary>Stores or replaces the server credential for a provider.</summary>
    /// <param name="providerId">Provider the credential belongs to.</param>
    /// <param name="request">The secret to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("Credential/{providerId}")]
    public async Task<ActionResult> SetCredential(
        string providerId,
        [FromBody] CredentialRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return BadRequest();
        }

        await _credentials
            .SetAsync(ProviderResolver.ServerCredentialOwner, providerId, request.ApiKey.Trim(), cancellationToken)
            .ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>Removes the server credential for a provider.</summary>
    /// <param name="providerId">Provider the credential belongs to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Credential/{providerId}")]
    public async Task<ActionResult> DeleteCredential(string providerId, CancellationToken cancellationToken)
    {
        await _credentials
            .DeleteAsync(ProviderResolver.ServerCredentialOwner, providerId, cancellationToken)
            .ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>A credential submission.</summary>
    public class CredentialRequest
    {
        /// <summary>Gets or sets the secret to store.</summary>
        public string ApiKey { get; set; } = string.Empty;
    }
}

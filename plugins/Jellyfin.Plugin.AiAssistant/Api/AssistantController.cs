using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiAssistant.Api.Dtos;
using Jellyfin.Plugin.AiAssistant.Assistant;
using Jellyfin.Plugin.AiAssistant.Configuration;
using Jellyfin.Plugin.AiAssistant.Providers;
using Jellyfin.Plugin.AiAssistant.Security;
using Jellyfin.Plugin.AiAssistant.Tools;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AiAssistant.Api;

/// <summary>
/// End-user assistant endpoints.
/// </summary>
/// <remarks>
/// Every action resolves the caller from the Jellyfin authorization context and acts
/// only within that identity. No endpoint accepts a user id, so one user's session
/// can never reach another user's conversations, settings or credentials.
/// </remarks>
[ApiController]
[Route("AiAssistant")]
[Produces(MediaTypeNames.Application.Json)]
public class AssistantController : ControllerBase
{
    private readonly IAuthorizationContext _authorizationContext;
    private readonly IUserManager _userManager;
    private readonly ConversationService _conversations;
    private readonly ConversationStore _store;
    private readonly ProviderResolver _resolver;
    private readonly IUserPreferenceStore _preferences;
    private readonly ICredentialStore _credentials;
    private readonly MetadataLanguageResolver _metadataLanguage;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssistantController"/> class.
    /// </summary>
    /// <param name="authorizationContext">Authorization context.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="conversations">Conversation service.</param>
    /// <param name="store">Conversation store.</param>
    /// <param name="resolver">Provider resolver.</param>
    /// <param name="preferences">User preference store.</param>
    /// <param name="credentials">Credential vault.</param>
    /// <param name="metadataLanguage">Metadata language resolver.</param>
    public AssistantController(
        IAuthorizationContext authorizationContext,
        IUserManager userManager,
        ConversationService conversations,
        ConversationStore store,
        ProviderResolver resolver,
        IUserPreferenceStore preferences,
        ICredentialStore credentials,
        MetadataLanguageResolver metadataLanguage)
    {
        _authorizationContext = authorizationContext;
        _userManager = userManager;
        _conversations = conversations;
        _store = store;
        _resolver = resolver;
        _preferences = preferences;
        _credentials = credentials;
        _metadataLanguage = metadataLanguage;
    }

    /// <summary>Gets the injected web client script.</summary>
    /// <returns>The JavaScript file.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    public ActionResult GetClientScript()
    {
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        var assembly = typeof(AssistantController).Assembly;
        var stream = assembly.GetManifestResourceStream(typeof(Plugin).Namespace + ".Web.aiAssistant.js");
        return stream is null ? NotFound() : File(stream, "text/javascript");
    }

    /// <summary>Reports whether the assistant is usable for the current user.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assistant status.</returns>
    [HttpGet("Status")]
    [Authorize]
    public async Task<ActionResult<AssistantStatusDto>> GetStatus(CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync().ConfigureAwait(false);
        if (scope is null)
        {
            return Unauthorized();
        }

        var status = new AssistantStatusDto
        {
            CanConfigure = Plugin.Config.AllowUserProviders,
            ServerLabel = Plugin.Config.ServerLabel
        };

        try
        {
            await _resolver.ResolveAsync(scope.UserId, cancellationToken).ConfigureAwait(false);
            status.Enabled = true;
        }
        catch (ProviderException ex)
        {
            status.Enabled = false;
            status.Reason = ex.Message;
        }

        return Ok(status);
    }

    /// <summary>Sends a message to the assistant.</summary>
    /// <param name="request">The user's message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assistant's reply.</returns>
    [HttpPost("Chat")]
    [Authorize]
    public async Task<ActionResult<ChatReplyDto>> Chat(
        [FromBody] ChatRequestDto request,
        CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync().ConfigureAwait(false);
        if (scope is null)
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest();
        }

        var conversationId = string.IsNullOrWhiteSpace(request.ConversationId)
            ? "default"
            : request.ConversationId;

        var history = _store.Get(scope.UserId, conversationId);

        var result = await _conversations
            .AskAsync(scope, history, request.Message.Trim(), cancellationToken)
            .ConfigureAwait(false);

        if (result.Pending is not null)
        {
            _store.SetPending(scope.UserId, conversationId, result.Pending);
            return Ok(new ChatReplyDto(result.Reply, true, NeedsConfirmation: true));
        }

        if (result.Success)
        {
            _store.Set(scope.UserId, conversationId, result.History);
        }

        return Ok(new ChatReplyDto(result.Reply, result.Success));
    }

    /// <summary>Answers a pending confirmation for a state-changing action.</summary>
    /// <param name="request">The user's answer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assistant's reply.</returns>
    [HttpPost("Chat/Confirm")]
    [Authorize]
    public async Task<ActionResult<ChatReplyDto>> Confirm(
        [FromBody] ConfirmRequestDto request,
        CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync().ConfigureAwait(false);
        if (scope is null)
        {
            return Unauthorized();
        }

        var conversationId = string.IsNullOrWhiteSpace(request.ConversationId)
            ? "default"
            : request.ConversationId;

        // Taken, not read: an approval is spent on use, so a replayed request cannot
        // run the same write twice.
        var pending = _store.TakePending(scope.UserId, conversationId);
        if (pending is null)
        {
            return Ok(new ChatReplyDto("That request is no longer waiting for an answer.", false));
        }

        var result = await _conversations
            .ResolveAsync(scope, pending, request.Approved, cancellationToken)
            .ConfigureAwait(false);

        if (result.Pending is not null)
        {
            _store.SetPending(scope.UserId, conversationId, result.Pending);
            return Ok(new ChatReplyDto(result.Reply, true, NeedsConfirmation: true));
        }

        if (result.Success)
        {
            _store.Set(scope.UserId, conversationId, result.History);
        }

        return Ok(new ChatReplyDto(result.Reply, result.Success));
    }

    /// <summary>Lists the current user's recent conversations.</summary>
    /// <returns>Conversations, newest first.</returns>
    [HttpGet("Conversations")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<ConversationSummaryDto>>> GetConversations()
    {
        var scope = await ResolveScopeAsync().ConfigureAwait(false);
        if (scope is null)
        {
            return Unauthorized();
        }

        // Scoped to the caller by construction — the store is keyed by user, and no
        // endpoint accepts a user id, so one session cannot list another's history.
        return Ok(_store.List(scope.UserId)
            .Select(c => new ConversationSummaryDto(c.Id, c.Title, c.Updated))
            .ToList());
    }

    /// <summary>Returns the visible turns of one conversation, for redisplay.</summary>
    /// <param name="conversationId">Conversation to read.</param>
    /// <returns>The turns a person would have seen.</returns>
    [HttpGet("Conversations/{conversationId}")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<TranscriptTurnDto>>> GetTranscript(string conversationId)
    {
        var scope = await ResolveScopeAsync().ConfigureAwait(false);
        if (scope is null)
        {
            return Unauthorized();
        }

        return Ok(_store.GetTranscript(scope.UserId, conversationId)
            .Select(t => new TranscriptTurnDto(t.Role, t.Text))
            .ToList());
    }

    /// <summary>Forgets a conversation.</summary>
    /// <param name="conversationId">The conversation to clear.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Chat/{conversationId}")]
    [Authorize]
    public async Task<ActionResult> ClearConversation(string conversationId)
    {
        var scope = await ResolveScopeAsync().ConfigureAwait(false);
        if (scope is null)
        {
            return Unauthorized();
        }

        _store.Clear(scope.UserId, conversationId);
        return NoContent();
    }

    /// <summary>Reads the current user's assistant settings.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The settings, without any secret.</returns>
    [HttpGet("Settings")]
    [Authorize]
    public async Task<ActionResult<UserSettingsDto>> GetSettings(CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync().ConfigureAwait(false);
        if (scope is null)
        {
            return Unauthorized();
        }

        var stored = await _preferences.GetAsync(scope.UserId, cancellationToken).ConfigureAwait(false);
        var dto = new UserSettingsDto
        {
            ProviderId = stored?.ProviderId ?? string.Empty,
            BaseUrl = stored?.BaseUrl ?? string.Empty,
            Model = stored?.Model ?? string.Empty,
            MetadataLanguage = stored?.MetadataLanguage ?? string.Empty,
            ServerMetadataLanguage = Plugin.Config.MetadataLanguage ?? string.Empty,
            AvailableProviders = _resolver.GetSelectableProviders()
                .Select(p => new ProviderInfoDto(p.Id, p.DisplayName, p.RequiresCredential))
                .ToList()
        };

        if (!string.IsNullOrEmpty(dto.ProviderId))
        {
            // A hint only. The stored key itself is never returned by any endpoint.
            dto.ApiKeyHint = await _credentials
                .GetHintAsync(scope.UserId, dto.ProviderId, cancellationToken)
                .ConfigureAwait(false);
        }

        return Ok(dto);
    }

    /// <summary>Updates the current user's assistant settings.</summary>
    /// <param name="settings">The new settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("Settings")]
    [Authorize]
    public async Task<ActionResult> SaveSettings(
        [FromBody] UserSettingsDto settings,
        CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync().ConfigureAwait(false);
        if (scope is null)
        {
            return Unauthorized();
        }

        if (!Plugin.Config.AllowUserProviders)
        {
            return Forbid();
        }

        // The administrator's allow-list is enforced here, not in the browser.
        if (!string.IsNullOrEmpty(settings.ProviderId) && !_resolver.IsPermitted(settings.ProviderId))
        {
            return BadRequest();
        }

        await _preferences.SetAsync(
            scope.UserId,
            new UserPreferences
            {
                ProviderId = settings.ProviderId,
                BaseUrl = settings.BaseUrl,
                Model = settings.Model,
                MetadataLanguage = settings.MetadataLanguage
            },
            cancellationToken).ConfigureAwait(false);

        _metadataLanguage.Invalidate(scope.UserId);

        // A blank key means "leave what is stored alone", so a user editing their model
        // does not have to retype a secret they cannot read back.
        if (!string.IsNullOrWhiteSpace(settings.ApiKey) && !string.IsNullOrEmpty(settings.ProviderId))
        {
            await _credentials
                .SetAsync(scope.UserId, settings.ProviderId, settings.ApiKey.Trim(), cancellationToken)
                .ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>Lists the models available from the user's configured provider.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Model identifiers.</returns>
    [HttpGet("Models")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<string>>> GetModels(CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync().ConfigureAwait(false);
        if (scope is null)
        {
            return Unauthorized();
        }

        try
        {
            // Deliberately the connection-level resolve: a user must be able to list
            // models before they have chosen one.
            var route = await _resolver.ResolveConnectionAsync(scope.UserId, cancellationToken).ConfigureAwait(false);
            var models = await route.Provider
                .ListModelsAsync(route.Connection, cancellationToken)
                .ConfigureAwait(false);
            return Ok(models);
        }
        catch (ProviderException)
        {
            return Ok(Array.Empty<string>());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Ok(Array.Empty<string>());
        }
    }

    private async Task<UserScope?> ResolveScopeAsync()
    {
        var auth = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (!auth.IsAuthenticated || auth.UserId == Guid.Empty)
        {
            return null;
        }

        var user = _userManager.GetUserById(auth.UserId);
        return user is null ? null : new UserScope(user);
    }
}

using System;
using System.Collections.Generic;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Plugin.MatureContent.Models;
using Jellyfin.Plugin.MatureContent.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MatureContent.Api;

/// <summary>
/// Mature content endpoints.
/// </summary>
[ApiController]
[Route("MatureContent")]
[Produces(MediaTypeNames.Application.Json)]
public class MatureContentController : ControllerBase
{
    private readonly IAuthorizationContext _authorizationContext;
    private readonly IUserManager _userManager;
    private readonly MaturePolicyService _policyService;

    /// <summary>
    /// Initializes a new instance of the <see cref="MatureContentController"/> class.
    /// </summary>
    /// <param name="authorizationContext">Authorization context.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="policyService">Mature policy service.</param>
    public MatureContentController(
        IAuthorizationContext authorizationContext,
        IUserManager userManager,
        MaturePolicyService policyService)
    {
        _authorizationContext = authorizationContext;
        _userManager = userManager;
        _policyService = policyService;
    }

    /// <summary>
    /// Gets the injected web client script.
    /// </summary>
    /// <returns>The JavaScript file.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    public ActionResult GetClientScript()
    {
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        Response.Headers.Pragma = "no-cache";
        return Serve("Web.matureContent.js", "text/javascript");
    }

    /// <summary>
    /// Gets current mature visibility state for the authenticated user.
    /// </summary>
    /// <response code="200">State returned.</response>
    /// <response code="401">No authenticated user.</response>
    /// <returns>The current user state.</returns>
    [HttpGet("State")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<MatureState>> GetState()
    {
        var user = await GetCurrentUserAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        return Ok(await _policyService.GetStateAsync(user).ConfigureAwait(false));
    }

    /// <summary>
    /// Updates current mature visibility state for the authenticated user.
    /// </summary>
    /// <param name="request">The requested state.</param>
    /// <response code="200">State updated.</response>
    /// <response code="403">The user is not allowed to toggle mature content.</response>
    /// <returns>The updated state.</returns>
    [HttpPost("State")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<MatureState>> UpdateState([FromBody] UpdateMatureStateRequest request)
    {
        var user = await GetCurrentUserAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        var state = await _policyService.GetStateAsync(user).ConfigureAwait(false);
        if (!state.CanToggle)
        {
            return Forbid();
        }

        await _policyService.SetMatureVisibleAsync(user, request.MatureVisible).ConfigureAwait(false);
        state.MatureVisible = request.MatureVisible; // policy read-back lags within the same request
        return Ok(state);
    }

    /// <summary>
    /// Lists users for the plugin configuration page.
    /// </summary>
    /// <response code="200">Users returned.</response>
    /// <returns>User rows.</returns>
    [HttpGet("Users")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<MatureUserInfo>> GetUsers()
        => Ok(_policyService.GetUsers());

    /// <summary>
    /// Sets whether a specific user can currently see mature content.
    /// </summary>
    /// <param name="userId">The user identifier.</param>
    /// <param name="request">The requested visibility.</param>
    /// <response code="200">Updated user row.</response>
    /// <response code="404">Unknown user.</response>
    /// <returns>The refreshed user row.</returns>
    [HttpPost("Users/{userId}/Visible")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MatureUserInfo>> SetUserVisible(
        [FromRoute] Guid userId,
        [FromBody] UpdateMatureStateRequest request)
    {
        var row = await _policyService.SetUserVisibleAsync(userId, request.MatureVisible).ConfigureAwait(false);
        return row is null ? NotFound() : Ok(row);
    }

    /// <summary>
    /// Lists every item currently marked as mature (the durable history).
    /// </summary>
    /// <response code="200">Marked items returned.</response>
    /// <returns>Item rows.</returns>
    [HttpGet("MarkedItems")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<MatureItemState>> GetMarkedItems()
        => Ok(_policyService.GetMarkedItems());

    /// <summary>
    /// Reconciles the durable history with the library: discovers already-tagged
    /// items, re-applies missing tags and drops dead ids.
    /// </summary>
    /// <response code="200">Sync summary.</response>
    /// <returns>The sync result.</returns>
    [HttpPost("Sync")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<MatureSyncResult>> Sync()
        => Ok(await _policyService.SyncAsync().ConfigureAwait(false));

    /// <summary>
    /// Applies locked defaults to users immediately after a configuration change.
    /// </summary>
    /// <response code="204">Defaults applied.</response>
    /// <returns>No content.</returns>
    [HttpPost("ApplyDefaults")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ApplyDefaults()
    {
        await _policyService.ApplyLockedDefaultsAsync().ConfigureAwait(false);
        return Ok(new { Applied = true });
    }

    /// <summary>
    /// Gets mature tag state for an item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <response code="200">Item state returned.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>The item state.</returns>
    [HttpGet("Items/{itemId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<MatureItemState> GetItemState([FromRoute] Guid itemId)
    {
        var state = _policyService.GetItemState(itemId);
        return state is null ? NotFound() : Ok(state);
    }

    /// <summary>
    /// Adds or removes mature tags from an item.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="request">The requested item state.</param>
    /// <response code="200">Item updated.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>The updated item state.</returns>
    [HttpPost("Items/{itemId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MatureItemState>> UpdateItemState(
        [FromRoute] Guid itemId,
        [FromBody] UpdateItemMatureRequest request)
    {
        var state = await _policyService.SetItemMatureAsync(itemId, request.IsMature).ConfigureAwait(false);
        return state is null ? NotFound() : Ok(state);
    }

    private async Task<Jellyfin.Database.Implementations.Entities.User?> GetCurrentUserAsync()
    {
        var auth = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (!auth.IsAuthenticated || auth.UserId == Guid.Empty)
        {
            return null;
        }

        return _userManager.GetUserById(auth.UserId);
    }

    private ActionResult Serve(string relativeName, string contentType)
    {
        var assembly = typeof(MatureContentController).Assembly;
        var resource = typeof(Plugin).Namespace + "." + relativeName;
        var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return NotFound(resource);
        }

        return File(stream, contentType);
    }
}

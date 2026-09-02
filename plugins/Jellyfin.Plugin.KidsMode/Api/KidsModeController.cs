using System;
using System.Collections.Generic;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Plugin.KidsMode.Models;
using Jellyfin.Plugin.KidsMode.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.KidsMode.Api;

/// <summary>
/// Kids mode endpoints.
/// </summary>
[ApiController]
[Route("KidsMode")]
[Produces(MediaTypeNames.Application.Json)]
public class KidsModeController : ControllerBase
{
    private readonly IAuthorizationContext _authorizationContext;
    private readonly IUserManager _userManager;
    private readonly KidsPolicyService _policyService;

    /// <summary>
    /// Initializes a new instance of the <see cref="KidsModeController"/> class.
    /// </summary>
    /// <param name="authorizationContext">Authorization context.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="policyService">Kids policy service.</param>
    public KidsModeController(
        IAuthorizationContext authorizationContext,
        IUserManager userManager,
        KidsPolicyService policyService)
    {
        _authorizationContext = authorizationContext;
        _userManager = userManager;
        _policyService = policyService;
    }

    /// <summary>Gets the injected web client script.</summary>
    /// <returns>The JavaScript file.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    public ActionResult GetClientScript()
    {
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        var assembly = typeof(KidsModeController).Assembly;
        var stream = assembly.GetManifestResourceStream(typeof(Plugin).Namespace + ".Web.kidsMode.js");
        return stream is null ? NotFound() : File(stream, "text/javascript");
    }

    /// <summary>Gets the current user's kids state.</summary>
    /// <returns>The state.</returns>
    [HttpGet("State")]
    [Authorize]
    public async Task<ActionResult<KidsState>> GetState()
    {
        var user = await CurrentUserAsync().ConfigureAwait(false);
        return user is null ? Unauthorized() : Ok(_policyService.GetState(user));
    }

    /// <summary>Activates or deactivates kids mode for the current user.</summary>
    /// <param name="request">The requested state.</param>
    /// <returns>The updated state.</returns>
    [HttpPost("State")]
    [Authorize]
    public async Task<ActionResult<KidsState>> UpdateState([FromBody] UpdateKidsStateRequest request)
    {
        var user = await CurrentUserAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        if (!_policyService.IsAvailable(user.Id))
        {
            return Forbid();
        }

        var state = await _policyService.SetActiveAsync(user.Id, request.Active).ConfigureAwait(false);
        return state is null ? NotFound() : Ok(state);
    }

    /// <summary>Gets whether an item is in the caller's relevant kids list.</summary>
    /// <param name="itemId">Item id.</param>
    /// <returns>The item state.</returns>
    [HttpGet("Items/{itemId}")]
    [Authorize]
    public async Task<ActionResult<KidsItemState>> GetItem([FromRoute] Guid itemId)
    {
        var user = await CurrentUserAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        var isAdmin = _userManager.GetUserById(user.Id) is { } u && _policyService.GetState(u).IsAdministrator;
        var state = _policyService.GetItemState(user.Id, isAdmin, itemId);
        return state is null ? NotFound() : Ok(state);
    }

    /// <summary>
    /// Toggles an item in a kids list. Administrators edit the global list; everyone
    /// else edits their own per-account override.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="request">The requested state.</param>
    /// <returns>The item state.</returns>
    [HttpPost("Items/{itemId}")]
    [Authorize]
    public async Task<ActionResult<KidsItemState>> SetItem([FromRoute] Guid itemId, [FromBody] UpdateKidsItemRequest request)
    {
        var user = await CurrentUserAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        if (!_policyService.IsAvailable(user.Id) && !_policyService.GetState(user).IsAdministrator)
        {
            return Forbid();
        }

        var isAdmin = _policyService.GetState(user).IsAdministrator;
        var state = isAdmin
            ? await _policyService.SetAdminItemAsync(itemId, request.InKids).ConfigureAwait(false)
            : await _policyService.SetUserItemAsync(user.Id, itemId, request.InKids).ConfigureAwait(false);
        return state is null ? NotFound() : Ok(state);
    }

    // ------------------------------------------------------------ admin

    /// <summary>Admin: lists users with their kids status.</summary>
    /// <returns>User rows.</returns>
    [HttpGet("Users")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<IReadOnlyList<KidsUserInfo>> GetUsers() => Ok(_policyService.GetUsers());

    /// <summary>Admin: sets whether kids mode is offered to a user.</summary>
    /// <param name="userId">User id.</param>
    /// <param name="request">The flag.</param>
    /// <returns>The refreshed user rows.</returns>
    [HttpPost("Users/{userId}/Enabled")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<IReadOnlyList<KidsUserInfo>>> SetUserEnabled(
        [FromRoute] Guid userId,
        [FromBody] UpdateKidsFlagRequest request)
    {
        await _policyService.SetAvailableAsync(userId, request.Value).ConfigureAwait(false);
        return Ok(_policyService.GetUsers());
    }

    /// <summary>Admin: forces a user in or out of kids mode.</summary>
    /// <param name="userId">User id.</param>
    /// <param name="request">The flag.</param>
    /// <returns>The refreshed user rows.</returns>
    [HttpPost("Users/{userId}/Active")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<IReadOnlyList<KidsUserInfo>>> SetUserActive(
        [FromRoute] Guid userId,
        [FromBody] UpdateKidsFlagRequest request)
    {
        await _policyService.SetActiveAsync(userId, request.Value).ConfigureAwait(false);
        return Ok(_policyService.GetUsers());
    }

    /// <summary>Admin: gets the global kids list.</summary>
    /// <returns>Item rows.</returns>
    [HttpGet("AdminItems")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<IReadOnlyList<KidsItemState>> GetAdminItems() => Ok(_policyService.GetAdminItems());

    /// <summary>Admin: gets a user's effective kids list with override sources.</summary>
    /// <param name="userId">User id.</param>
    /// <returns>Item rows.</returns>
    [HttpGet("Users/{userId}/Items")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<IReadOnlyList<KidsItemState>> GetUserItems([FromRoute] Guid userId)
        => Ok(_policyService.GetUserItems(userId));

    /// <summary>Admin: toggles an item in a specific user's override.</summary>
    /// <param name="userId">User id.</param>
    /// <param name="itemId">Item id.</param>
    /// <param name="request">The requested state.</param>
    /// <returns>The item state.</returns>
    [HttpPost("Users/{userId}/Items/{itemId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<KidsItemState>> SetUserItem(
        [FromRoute] Guid userId,
        [FromRoute] Guid itemId,
        [FromBody] UpdateKidsItemRequest request)
    {
        var state = await _policyService.SetUserItemAsync(userId, itemId, request.InKids).ConfigureAwait(false);
        return state is null ? NotFound() : Ok(state);
    }

    /// <summary>Admin: reconciles lists and tags with the library.</summary>
    /// <returns>Sync summary.</returns>
    [HttpPost("Sync")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<KidsSyncResult>> Sync()
        => Ok(await _policyService.SyncAsync().ConfigureAwait(false));

    private async Task<Jellyfin.Database.Implementations.Entities.User?> CurrentUserAsync()
    {
        var auth = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (!auth.IsAuthenticated || auth.UserId == Guid.Empty)
        {
            return null;
        }

        return _userManager.GetUserById(auth.UserId);
    }
}

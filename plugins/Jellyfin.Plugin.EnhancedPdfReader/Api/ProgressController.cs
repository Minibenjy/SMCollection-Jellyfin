using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;
using Jellyfin.Plugin.EnhancedPdfReader.Progress;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EnhancedPdfReader.Api;

/// <summary>
/// Reads and writes the reading position of the calling user.
/// </summary>
[ApiController]
[Authorize]
[Route("EnhancedPdfReader/Progress")]
[Produces(MediaTypeNames.Application.Json)]
public class ProgressController : ControllerBase
{
    private readonly ProgressStore _store;
    private readonly IAuthorizationContext _authContext;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<ProgressController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProgressController"/> class.
    /// </summary>
    /// <param name="store">The progress store.</param>
    /// <param name="authContext">Instance of the <see cref="IAuthorizationContext"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ProgressController}"/> interface.</param>
    public ProgressController(
        ProgressStore store,
        IAuthorizationContext authContext,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        ILogger<ProgressController> logger)
    {
        _store = store;
        _authContext = authContext;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the calling user's reading position for an item.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="userId">Optional user id, only honoured for API-key callers (admin tooling).</param>
    /// <returns>The stored position; Page is 0 when nothing is stored.</returns>
    [HttpGet("{itemId}")]
    public async Task<ActionResult<ReadingPosition>> GetProgress([FromRoute] Guid itemId, [FromQuery] Guid? userId)
    {
        var uid = await ResolveUserAsync(userId).ConfigureAwait(false);
        if (uid.Equals(default))
        {
            _logger.LogWarning("[EnhancedPdfReader] GET progress for {ItemId} without a user, refused", itemId);
            return Forbid();
        }

        var pos = _store.Get(uid, itemId) ?? new ReadingPosition();
        _logger.LogInformation("[EnhancedPdfReader] GET progress user={UserId} item={ItemId} -> page {Page}", uid, itemId, pos.Page);
        return pos;
    }

    /// <summary>
    /// Saves the calling user's reading position for an item.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="body">The new position.</param>
    /// <param name="userId">Optional user id, only honoured for API-key callers (admin tooling).</param>
    /// <returns>The stored position.</returns>
    [HttpPost("{itemId}")]
    public async Task<ActionResult<ReadingPosition>> SetProgress(
        [FromRoute] Guid itemId,
        [FromBody] ReadingPosition body,
        [FromQuery] Guid? userId)
    {
        var uid = await ResolveUserAsync(userId).ConfigureAwait(false);
        if (uid.Equals(default))
        {
            _logger.LogWarning("[EnhancedPdfReader] POST progress for {ItemId} without a user, refused", itemId);
            return Forbid();
        }

        var page = body?.Page ?? 0;
        var numPages = body?.NumPages ?? 0;
        var pos = _store.Set(uid, itemId, page, numPages);
        _logger.LogInformation("[EnhancedPdfReader] POST progress user={UserId} item={ItemId} page {Page}/{NumPages}", uid, itemId, page, numPages);
        SyncNativeUserData(uid, itemId, page, numPages);
        return pos;
    }

    /// <summary>
    /// Mirrors the position into Jellyfin's own per-user playstate so the book shows up
    /// in the "Continue reading" home section, with a sensible progress bar.
    /// </summary>
    private void SyncNativeUserData(Guid userId, Guid itemId, int page, int numPages)
    {
        try
        {
            var user = _userManager.GetUserById(userId);
            var item = _libraryManager.GetItemById(itemId);
            if (user is null || item is null)
            {
                return;
            }

            var data = _userDataManager.GetUserData(user, item);
            if (data is null)
            {
                return;
            }

            var runtime = item.RunTimeTicks ?? 0;
            if (page <= 0)
            {
                data.PlaybackPositionTicks = 0;
            }
            else if (numPages > 0 && page >= numPages)
            {
                // finished: drop it from "continue reading" and mark it as read
                data.PlaybackPositionTicks = 0;
                data.Played = true;
                if (data.PlayCount < 1)
                {
                    data.PlayCount = 1;
                }
            }
            else if (runtime > 0 && numPages > 0)
            {
                // scale the page onto the item's runtime so PlayedPercentage is the real progress
                var ticks = (long)Math.Round(runtime * ((double)page / numPages));
                data.PlaybackPositionTicks = Math.Clamp(ticks, 1, runtime);
                data.Played = false;
            }
            else
            {
                data.PlaybackPositionTicks = page * 10000L;
                data.Played = false;
            }

            if (page > 0)
            {
                data.LastPlayedDate = DateTime.UtcNow;
            }

            _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.UpdateUserData, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[EnhancedPdfReader] could not mirror progress into playstate for {ItemId}", itemId);
        }
    }

    private async Task<Guid> ResolveUserAsync(Guid? requested)
    {
        Guid uid = default;
        try
        {
            var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
            uid = auth.UserId;
        }
        catch (Exception)
        {
            uid = default;
        }

        // API keys are not tied to a user; let those callers (admin tooling, tests) name one explicitly.
        if (uid.Equals(default) && requested.HasValue)
        {
            uid = requested.Value;
        }

        return uid;
    }
}

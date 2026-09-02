using System.Collections.Generic;
using Jellyfin.Plugin.AutoThumbnails.Services;
using System.Net.Mime;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AutoThumbnails.Api;

/// <summary>
/// Drives thumbnail runs from the plugin's dashboard page.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("AutoThumbnails")]
[Produces(MediaTypeNames.Application.Json)]
public class AutoThumbnailsController : ControllerBase
{
    private readonly ThumbnailJobService _jobs;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoThumbnailsController"/> class.
    /// </summary>
    /// <param name="jobs">The job service.</param>
    public AutoThumbnailsController(ThumbnailJobService jobs)
    {
        _jobs = jobs;
    }

    /// <summary>
    /// Lists the libraries that a run can be limited to.
    /// </summary>
    /// <response code="200">Libraries returned.</response>
    /// <returns>The libraries.</returns>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LibraryInfo>> GetLibraries()
        => Ok(_jobs.GetLibraries());

    /// <summary>
    /// Gets the progress and log of the current or last run.
    /// </summary>
    /// <param name="since">The highest log sequence the caller already holds.</param>
    /// <response code="200">Status returned.</response>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<JobStatus> GetStatus([FromQuery] long since = 0)
        => Ok(_jobs.GetStatus(since));

    /// <summary>
    /// Starts a run.
    /// </summary>
    /// <param name="request">What the run should cover.</param>
    /// <response code="200">The run started.</response>
    /// <response code="409">A run is already in progress.</response>
    /// <returns>The status of the started run.</returns>
    [HttpPost("Start")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<JobStatus> Start([FromBody] JobRequest request)
        => _jobs.TryStart(request ?? new JobRequest())
            ? Ok(_jobs.GetStatus(0))
            : Conflict();

    /// <summary>
    /// Asks the current run to stop.
    /// </summary>
    /// <response code="204">Cancellation requested.</response>
    /// <returns>No content.</returns>
    [HttpPost("Cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult Cancel()
    {
        _jobs.Cancel();
        return NoContent();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DiscoverHome.Artwork;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DiscoverHome.Api;

/// <summary>
/// Endpoints backing the DiscoverHome client layer.
/// </summary>
[ApiController]
[Route("DiscoverHome")]
public class DiscoverHomeController : ControllerBase
{
    private readonly StudioArtworkCache _artwork;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoverHomeController"/> class.
    /// </summary>
    /// <param name="artwork">Studio artwork cache.</param>
    public DiscoverHomeController(StudioArtworkCache artwork)
    {
        _artwork = artwork;
    }

    /// <summary>Serves the client script injected into the web client.</summary>
    /// <returns>The script.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    public ActionResult GetClientScript() => Embedded("Web.discoverHome.js", "text/javascript");

    /// <summary>Serves the stylesheet the client script links.</summary>
    /// <returns>The stylesheet.</returns>
    [HttpGet("Style")]
    [AllowAnonymous]
    [Produces("text/css")]
    public ActionResult GetStyle() => Embedded("Web.discoverHome.css", "text/css");

    /// <summary>Serves the presentation settings the client script needs.</summary>
    /// <returns>The settings.</returns>
    /// <remarks>
    /// Only presentation values are exposed. Nothing here is a permission decision, so
    /// there is no per-user filtering to do — but it still requires a signed-in user,
    /// since an anonymous caller has no home screen to style.
    /// </remarks>
    [HttpGet("Settings")]
    [Authorize]
    public ActionResult<object> GetSettings()
    {
        var c = Plugin.Config;
        return Ok(new
        {
            c.PinSidebar,
            c.SidebarBreakpoint,
            c.ReorderSidebar,
            c.LogoInSidebar,
            c.CenteredSearch,
            c.HideRandomButton,
            c.CompactCards,
            c.CardWidth,
            c.ShowTypeBadges,
            c.AccentColor,
            c.LabelMovie,
            c.LabelSeries,
            c.LabelEpisode,
            c.ShuffleSections,
            c.EnableGenreRow,
            c.EnableStudioRow,
            c.MinRowGap,
            c.MaxRowGap,
            c.ChannelRowSize,
            c.EnableStudioLogos,
            c.EnableGenreCollages
        });
    }

    /// <summary>Serves a cached studio logo.</summary>
    /// <param name="name">The studio name.</param>
    /// <returns>The logo, or 404 when this studio has none cached.</returns>
    /// <remarks>
    /// Takes a studio *name* and derives the path server-side, so no caller-supplied
    /// string ever reaches the filesystem as a path. Anonymous because an
    /// <c>&lt;img&gt;</c> tag cannot send an auth header, and the payload is a public
    /// company logo — the same bytes the upstream repository serves to anyone.
    /// </remarks>
    [HttpGet("Art/Studio")]
    [AllowAnonymous]
    public ActionResult GetStudioLogo([FromQuery] string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BadRequest();
        }

        var path = _artwork.GetCachePath(name);
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=86400";
        return PhysicalFile(path, "image/jpeg");
    }

    /// <summary>Lists the studios that have a cached logo.</summary>
    /// <returns>Studio names.</returns>
    [HttpGet("Art/Studios")]
    [Authorize]
    public ActionResult<IReadOnlyList<string>> GetAvailableStudios()
    {
        // Short client-side cache: the list only changes when a sync runs.
        Response.Headers.CacheControl = "private, max-age=600";
        return Ok(_artwork.GetAvailableStudios());
    }

    /// <summary>Runs the artwork sync immediately instead of waiting for the daily task.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many logos are cached afterwards.</returns>
    [HttpPost("Admin/SyncArtwork")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<object>> SyncArtwork(CancellationToken cancellationToken)
    {
        var total = await _artwork
            .SyncAsync(new Progress<double>(), cancellationToken)
            .ConfigureAwait(false);

        return Ok(new { cached = total });
    }

    private ActionResult Embedded(string relativeName, string contentType)
    {
        // no-store: these two files change with every plugin upgrade, and a stale
        // cached copy would silently pin the UI to an older version.
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";

        var assembly = typeof(DiscoverHomeController).Assembly;
        var stream = assembly.GetManifestResourceStream(typeof(Plugin).Namespace + "." + relativeName);
        return stream is null ? NotFound() : File(stream, contentType);
    }
}

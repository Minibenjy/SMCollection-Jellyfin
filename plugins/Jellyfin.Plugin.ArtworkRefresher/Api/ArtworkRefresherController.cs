using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ArtworkRefresher.Configuration;
using Jellyfin.Plugin.ArtworkRefresher.Core;
using Jellyfin.Plugin.ArtworkRefresher.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.ArtworkRefresher.Api;

/// <summary>What the browser needs to know about the plugin and the caller.</summary>
public sealed class CapabilitiesResponse
{
    /// <summary>Gets or sets a value indicating whether the caller is an administrator.</summary>
    public bool IsAdmin { get; set; }

    /// <summary>Gets or sets the plugin version.</summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the caller may refresh one item.</summary>
    public bool SupportsItemRefresh { get; set; }

    /// <summary>Gets or sets the image types that rotate on every page load (the browser script rewrites those images).</summary>
    public string[] RotationPerLoadTypes { get; set; } = [];

    /// <summary>Gets or sets the optional integrations found (administrators only).</summary>
    public Dictionary<string, bool>? Integrations { get; set; }
}

/// <summary>The item ids the browser asks about.</summary>
public sealed class RotatingAvailableRequest
{
    /// <summary>Gets or sets the item ids.</summary>
    public string[] Ids { get; set; } = [];
}

/// <summary>The configuration page's view of the settings.</summary>
public sealed class ConfigurationResponse
{
    /// <summary>Gets or sets the configuration, with keys masked.</summary>
    public PluginConfiguration Configuration { get; set; } = new();

    /// <summary>Gets or sets the scheduler state.</summary>
    public SchedulerState Scheduler { get; set; } = new();
}

/// <summary>
/// REST endpoints of Artwork Refresher. Everything that changes anything needs an administrator,
/// except the single-item refresh, which an administrator can open to users for the items they can see.
/// </summary>
[ApiController]
[Route("ArtworkRefresher")]
[Produces(MediaTypeNames.Application.Json)]
public class ArtworkRefresherController : ControllerBase
{
    private readonly ArtworkRefreshService _service;
    private readonly HomeArtworkService _home;
    private readonly OptionalIntegrationDetector _detector;
    private readonly IAuthorizationContext _authorizationContext;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtworkRefresherController"/> class.
    /// </summary>
    /// <param name="service">The refresh service.</param>
    /// <param name="home">The Home service.</param>
    /// <param name="detector">The integration detector.</param>
    /// <param name="authorizationContext">The authorization context.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    public ArtworkRefresherController(
        ArtworkRefreshService service,
        HomeArtworkService home,
        OptionalIntegrationDetector detector,
        IAuthorizationContext authorizationContext,
        IUserManager userManager,
        ILibraryManager libraryManager)
    {
        _service = service;
        _home = home;
        _detector = detector;
        _authorizationContext = authorizationContext;
        _userManager = userManager;
        _libraryManager = libraryManager;
    }

    /// <summary>Gets the injected web client script.</summary>
    /// <returns>The JavaScript file.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    public ActionResult GetClientScript()
    {
        Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
        var stream = typeof(ArtworkRefresherController).Assembly.GetManifestResourceStream(typeof(Plugin).Namespace + ".Web.artworkRefresher.js");
        return stream is null ? NotFound() : File(stream, "text/javascript");
    }

    /// <summary>Gets what the caller may do.</summary>
    /// <response code="200">Capabilities returned.</response>
    /// <returns>The capabilities.</returns>
    [HttpGet("Capabilities")]
    [Authorize]
    public async Task<ActionResult<CapabilitiesResponse>> GetCapabilities()
    {
        var (user, isAdmin) = await CallerAsync().ConfigureAwait(false);
        if (user is null && !isAdmin)
        {
            return Unauthorized();
        }

        return Ok(new CapabilitiesResponse
        {
            IsAdmin = isAdmin,
            PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty,
            SupportsItemRefresh = isAdmin || Plugin.Config.AllowNonAdminItemRefresh,
            RotationPerLoadTypes = Plugin.Config.Enabled && !Plugin.Config.DryRun ? RotationPolicy.PerLoadTypes(Plugin.Config) : [],
            Integrations = isAdmin ? _detector.Detect().ToDictionary(k => k.Key, k => k.Value) : null
        });
    }

    /// <summary>Gets the settings with the keys masked.</summary>
    /// <response code="200">Settings returned.</response>
    /// <returns>The settings.</returns>
    [HttpGet("Configuration")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<ConfigurationResponse> GetConfiguration()
    {
        var c = Plugin.Config;
        var copy = Clone(c);
        copy.Sources = ConfigurationMasking.MaskedCopy(c.Sources);
        return Ok(new ConfigurationResponse { Configuration = copy, Scheduler = _service.State.Use(d => d.Scheduler) });
    }

    /// <summary>Saves the settings. A key sent as *** keeps the stored one; an empty key deletes it.</summary>
    /// <param name="incoming">The settings from the page.</param>
    /// <response code="204">Saved.</response>
    /// <returns>No content.</returns>
    [HttpPost("Configuration")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SaveConfiguration([FromBody] PluginConfiguration incoming)
    {
        var plugin = Plugin.Instance;
        if (plugin is null || incoming is null)
        {
            return BadRequest();
        }

        var stored = plugin.Configuration;
        incoming.Sources = ConfigurationMasking.Merge(stored.Sources, incoming.Sources ?? new SourceConfiguration());
        Sanitize(incoming);
        plugin.UpdateConfiguration(incoming);
        return NoContent();
    }

    /// <summary>Lists the libraries.</summary>
    /// <response code="200">Libraries returned.</response>
    /// <returns>The libraries.</returns>
    [HttpGet("Libraries")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<IReadOnlyList<LibraryInfo>> GetLibraries() => Ok(_service.GetLibraries());

    /// <summary>Gets the progress and log of the current or last run.</summary>
    /// <param name="since">The highest log sequence the caller already holds.</param>
    /// <response code="200">Status returned.</response>
    /// <returns>The status.</returns>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<RunStatus> GetStatus([FromQuery] long since = 0) => Ok(_service.GetStatus(since));

    /// <summary>Starts a run now, ignoring the time window.</summary>
    /// <param name="request">What to do.</param>
    /// <response code="200">Started.</response>
    /// <response code="409">A run is already in progress.</response>
    /// <returns>The status.</returns>
    [HttpPost("Run")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<RunStatus> Run([FromBody] RunRequest? request)
        => _service.TryStart(new RunRequest
        {
            Scheduled = false,
            DryRun = request?.DryRun,
            Mode = request?.Mode,
            LibraryIds = request?.LibraryIds ?? [],
            FillMetadata = request?.FillMetadata
        })
            ? Ok(_service.GetStatus(0))
            : Conflict();

    /// <summary>Asks the current run to stop.</summary>
    /// <response code="204">Cancellation requested.</response>
    /// <returns>No content.</returns>
    [HttpPost("Cancel")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Cancel()
    {
        _service.Cancel();
        return NoContent();
    }

    /// <summary>Refreshes the artwork of one item (the card menu).</summary>
    /// <param name="id">The item id.</param>
    /// <param name="request">What to do.</param>
    /// <response code="200">Done; the body says what happened to each slot.</response>
    /// <response code="403">The caller may not refresh items.</response>
    /// <response code="404">There is no such item (or the caller cannot see it).</response>
    /// <response code="429">A non-administrator asked again too soon.</response>
    /// <returns>The outcome.</returns>
    [HttpPost("Item/{id}")]
    [Authorize]
    public async Task<ActionResult<ItemRefreshResult>> RefreshItem([FromRoute] Guid id, [FromBody] ItemRefreshRequest? request, CancellationToken cancellationToken)
    {
        var (user, isAdmin) = await CallerAsync().ConfigureAwait(false);
        request ??= new ItemRefreshRequest();
        var configuration = Plugin.Config;

        BaseItem? item;
        if (isAdmin)
        {
            item = _libraryManager.GetItemById(id);
        }
        else
        {
            if (user is null || !configuration.AllowNonAdminItemRefresh)
            {
                return Forbid();
            }

            // Looked up as the user, so a guessed id cannot reach an item they are not allowed to see.
            item = _libraryManager.GetItemById<BaseItem>(id, user);

            // No force and no replacing for non-administrators, whatever the body says.
            request.Force = false;
            request.OverrideExcludedLibraries = false;
            request.Mode = ArtworkRefreshMode.MissingOnly;

            var key = user.Id.ToString("N") + ":" + id.ToString("N");
            var cooldown = TimeSpan.FromMinutes(Math.Max(0, configuration.NonAdminRefreshCooldownMinutes));
            var allowed = _service.State.Use(d =>
            {
                if (d.UserRefreshes.TryGetValue(key, out var last) && DateTimeOffset.UtcNow - last < cooldown)
                {
                    return false;
                }

                d.UserRefreshes[key] = DateTimeOffset.UtcNow;
                return true;
            });
            if (!allowed)
            {
                return StatusCode(StatusCodes.Status429TooManyRequests);
            }
        }

        if (item is null)
        {
            return NotFound();
        }

        return Ok(await _service.RefreshItemAsync(item, request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Serves one image of the pool of an item, a different one on each page view (rotation per page load).
    /// The address carries a signed token handed out by the authenticated "available" call, because an
    /// img tag cannot send a header: the token authorises one item and type for two hours, so item ids
    /// cannot be guessed and anonymous visitors cannot make the server download anything.
    /// </summary>
    /// <param name="id">The item id.</param>
    /// <param name="type">The image type.</param>
    /// <param name="s">The signed token.</param>
    /// <param name="n">An id of the page view; the same id always gets the same image.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The image.</response>
    /// <response code="404">This slot does not rotate or the token is wrong; keep the normal image.</response>
    /// <returns>The image.</returns>
    [HttpGet("Rotating/{id}/{type}")]
    [AllowAnonymous]
    [Produces("image/jpeg", "image/png", "image/webp")]
    public async Task<ActionResult> GetRotatingImage([FromRoute] Guid id, [FromRoute] string type, [FromQuery] string? s, [FromQuery] string? n, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<MediaBrowser.Model.Entities.ImageType>(type, true, out var imageType)
            || imageType is not (MediaBrowser.Model.Entities.ImageType.Primary or MediaBrowser.Model.Entities.ImageType.Backdrop
                or MediaBrowser.Model.Entities.ImageType.Logo or MediaBrowser.Model.Entities.ImageType.Thumb))
        {
            return NotFound();
        }

        var image = await _service.ServeRotatingAsync(id, imageType, s, n, cancellationToken).ConfigureAwait(false);
        if (image is null)
        {
            return NotFound();
        }

        // Private and short: the preload and the display of this page view share it, the next page view has another n.
        Response.Headers.CacheControl = "private, max-age=300";
        return PhysicalFile(image.Path, image.MimeType);
    }

    /// <summary>Tells which of the given items have rotating images and signs their addresses.</summary>
    /// <param name="request">The item ids.</param>
    /// <response code="200">The keys (id:Type with the id as 32 hex digits) and their tokens.</response>
    /// <returns>The keys and tokens.</returns>
    [HttpPost("Rotating/Available")]
    [Authorize]
    public async Task<ActionResult> GetRotatingAvailable([FromBody] RotatingAvailableRequest? request)
    {
        var (user, isAdmin) = await CallerAsync().ConfigureAwait(false);
        if (user is null && !isAdmin)
        {
            return Unauthorized();
        }

        var ids = (request?.Ids ?? []).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().Take(300).ToList();
        if (!isAdmin && user is not null)
        {
            // Only items this user can see.
            ids = ids.Where(i => _libraryManager.GetItemById<BaseItem>(i, user) is not null).ToList();
        }

        return Ok(new { keys = _service.RotatableTokens(ids) });
    }

    /// <summary>Puts back the image an item had before the first daily rotation and locks that image type of the item.</summary>
    /// <param name="id">The item id.</param>
    /// <param name="type">The image type.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Restored.</response>
    /// <response code="404">No such item, or no backup of that image.</response>
    /// <returns>No content.</returns>
    [HttpPost("Rotating/Restore/{id}/{type}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> RestoreOriginal([FromRoute] Guid id, [FromRoute] string type, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(id);
        if (item is null || !Enum.TryParse<MediaBrowser.Model.Entities.ImageType>(type, true, out var imageType))
        {
            return NotFound();
        }

        return await _service.RestoreOriginalAsync(item, imageType, cancellationToken).ConfigureAwait(false) ? NoContent() : NotFound();
    }

    /// <summary>Lists the image locks.</summary>
    /// <response code="200">Locks returned.</response>
    /// <returns>The locks.</returns>
    [HttpGet("Locks")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<IReadOnlyList<ImageLockRule>> GetLocks() => Ok(Plugin.Config.ImageLocks);

    /// <summary>Adds an image lock for a library or an item.</summary>
    /// <param name="rule">The lock.</param>
    /// <response code="204">Saved.</response>
    /// <returns>No content.</returns>
    [HttpPut("Locks")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult PutLock([FromBody] ImageLockRule rule)
    {
        var plugin = Plugin.Instance;
        if (plugin is null || rule is null || rule.TargetId == Guid.Empty)
        {
            return BadRequest();
        }

        var config = plugin.Configuration;
        config.ImageLocks = config.ImageLocks
            .Where(r => !(r.TargetId == rule.TargetId && string.Equals(r.ImageType, rule.ImageType, StringComparison.OrdinalIgnoreCase)))
            .Append(rule)
            .ToArray();
        plugin.Save();
        return NoContent();
    }

    /// <summary>Removes the locks of a target.</summary>
    /// <param name="targetId">The library or item id.</param>
    /// <param name="imageType">Only this image type, or all when omitted.</param>
    /// <response code="204">Removed.</response>
    /// <returns>No content.</returns>
    [HttpDelete("Locks/{targetId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult DeleteLock([FromRoute] Guid targetId, [FromQuery] string? imageType = null)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return BadRequest();
        }

        var config = plugin.Configuration;
        config.ImageLocks = config.ImageLocks
            .Where(r => !(r.TargetId == targetId && (imageType is null || string.Equals(r.ImageType, imageType, StringComparison.OrdinalIgnoreCase))))
            .ToArray();
        plugin.Save();
        return NoContent();
    }

    /// <summary>Lists the Home sections a user can get an image for.</summary>
    /// <param name="userId">The user; the caller by default.</param>
    /// <response code="200">Sections returned.</response>
    /// <returns>The sections with the address of their images.</returns>
    [HttpGet("Home/Sections")]
    [Authorize]
    public async Task<ActionResult> GetHomeSections([FromQuery] Guid? userId)
    {
        var (user, isAdmin) = await CallerAsync().ConfigureAwait(false);
        var target = ResolveTarget(user, isAdmin, userId);
        if (target is null)
        {
            return Forbid();
        }

        var sections = _home.GetSections(target.Value);
        if (sections is null)
        {
            return NotFound();
        }

        return Ok(new
        {
            sections = sections.Select(s => new
            {
                key = s.Key,
                name = s.Name,
                imageUrl = "ArtworkRefresher/Home/" + Uri.EscapeDataString(s.Key) + "/Image?userId=" + target.Value.ToString("N")
            })
        });
    }

    /// <summary>Gets the mosaic of a Home section for a user.</summary>
    /// <param name="sectionKey">The section key.</param>
    /// <param name="userId">The user; the caller by default.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The image.</response>
    /// <response code="304">Not modified.</response>
    /// <response code="404">Nothing to show.</response>
    /// <returns>The JPEG.</returns>
    [HttpGet("Home/{sectionKey}/Image")]
    [Authorize]
    [Produces("image/jpeg")]
    public async Task<ActionResult> GetHomeImage([FromRoute] string sectionKey, [FromQuery] Guid? userId, CancellationToken cancellationToken)
    {
        var (user, isAdmin) = await CallerAsync().ConfigureAwait(false);
        var target = ResolveTarget(user, isAdmin, userId);
        if (target is null)
        {
            return Forbid();
        }

        var image = await _home.GetImageAsync(target.Value, sectionKey, cancellationToken).ConfigureAwait(false);
        if (image is null)
        {
            return NotFound();
        }

        // Private and keyed by the user: one user's image is never a shared cache entry.
        Response.Headers.CacheControl = "private, max-age=3600";
        Response.Headers.Vary = "Authorization";
        Response.Headers.ETag = image.Value.ETag;
        if (Request.Headers.IfNoneMatch.Contains(image.Value.ETag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return PhysicalFile(image.Value.Path, "image/jpeg");
    }

    private static PluginConfiguration Clone(PluginConfiguration c)
    {
        var options = new System.Text.Json.JsonSerializerOptions();
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return System.Text.Json.JsonSerializer.Deserialize<PluginConfiguration>(System.Text.Json.JsonSerializer.Serialize(c, options), options) ?? new PluginConfiguration();
    }

    private static void Sanitize(PluginConfiguration c)
    {
        c.RefreshIntervalHours = Math.Clamp(c.RefreshIntervalHours, 1, 24 * 90);
        c.MaxRunMinutes = Math.Clamp(c.MaxRunMinutes, 0, 24 * 60);
        c.MinimumImageWidth = Math.Clamp(c.MinimumImageWidth, 1, 10000);
        c.MinimumImageHeight = Math.Clamp(c.MinimumImageHeight, 1, 10000);
        c.MaximumDownloadBytes = Math.Clamp(c.MaximumDownloadBytes, 100 * 1024, 100L * 1024 * 1024);
        c.HttpTimeoutSeconds = Math.Clamp(c.HttpTimeoutSeconds, 5, 300);
        c.MaximumRedirects = Math.Clamp(c.MaximumRedirects, 0, 5);
        c.MosaicColumns = Math.Clamp(c.MosaicColumns, 1, 6);
        c.MosaicRows = Math.Clamp(c.MosaicRows, 1, 6);
        c.MosaicWidth = Math.Clamp(c.MosaicWidth, 200, 4000);
        c.MosaicHeight = Math.Clamp(c.MosaicHeight, 200, 4000);
        c.JpegQuality = Math.Clamp(c.JpegQuality, 40, 100);
        c.ReplaceRotationPool = Math.Clamp(c.ReplaceRotationPool, 1, 10);
        c.NonAdminRefreshCooldownMinutes = Math.Clamp(c.NonAdminRefreshCooldownMinutes, 0, 24 * 60);
        c.MosaicRotationDays = Math.Clamp(c.MosaicRotationDays, 1, 365);
        c.RetryAfterDays = Math.Clamp(c.RetryAfterDays, 1, 365);
        c.DefaultPoolSize = Math.Clamp(c.DefaultPoolSize, RotationPolicy.MinPool, RotationPolicy.MaxPool);
        c.PoolRefreshDays = Math.Clamp(c.PoolRefreshDays, 1, 365);
        c.PoolCacheMaxMegabytes = Math.Clamp(c.PoolCacheMaxMegabytes, 16, 102400);
        c.RecentlyAddedDays = Math.Clamp(c.RecentlyAddedDays, 1, 365);
        c.NewItemDelaySeconds = Math.Clamp(c.NewItemDelaySeconds, 0, 3600);
        c.Rotation = (c.Rotation ?? [])
            .Where(r => Enum.TryParse<MediaBrowser.Model.Entities.ImageType>(r.ImageType, true, out var t)
                        && t is MediaBrowser.Model.Entities.ImageType.Primary or MediaBrowser.Model.Entities.ImageType.Backdrop
                            or MediaBrowser.Model.Entities.ImageType.Logo or MediaBrowser.Model.Entities.ImageType.Thumb)
            .GroupBy(r => r.ImageType, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToArray();
        foreach (var r in c.Rotation)
        {
            r.PoolSize = r.PoolSize <= 0 ? 0 : Math.Clamp(r.PoolSize, RotationPolicy.MinPool, RotationPolicy.MaxPool);
        }

        c.MetadataFieldsToFill = (c.MetadataFieldsToFill ?? [])
            .Where(f => MetadataGapPolicy.AllFields.Contains(f, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        c.MetadataBookLibraryOptIn ??= [];
        c.EnabledImageTypes ??= [];
        c.IncludedLibraryIds ??= [];
        c.ExcludedLibraryIds ??= [];
        c.ImageLocks ??= [];
        c.SourceOrder ??= [];
        c.PreferredLanguages ??= [];
        c.SensitiveLibraryIds ??= [];
        c.SensitiveTags ??= [];
        c.AdditionalAllowedHosts ??= [];
        if (!RefreshWindowPolicy.TryParseTime(c.RefreshWindowStart, out _))
        {
            c.RefreshWindowStart = "02:00";
        }

        if (!RefreshWindowPolicy.TryParseTime(c.RefreshWindowEnd, out _))
        {
            c.RefreshWindowEnd = "06:00";
        }
    }

    private static Guid? ResolveTarget(User? caller, bool isAdmin, Guid? requested)
    {
        if (requested is { } r && r != Guid.Empty)
        {
            // Only an administrator may ask for somebody else's Home images.
            return isAdmin || caller?.Id == r ? r : null;
        }

        return caller?.Id;
    }

    private async Task<(User? User, bool IsAdmin)> CallerAsync()
    {
        var auth = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (!auth.IsAuthenticated)
        {
            return (null, false);
        }

        if (auth.UserId == Guid.Empty)
        {
            // An API key (no user) is an administrator credential.
            return (null, auth.IsApiKey);
        }

        var user = _userManager.GetUserById(auth.UserId);
        return (user, user is not null && (_userManager.GetUserDto(user, string.Empty).Policy?.IsAdministrator ?? false));
    }
}

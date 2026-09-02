using System.IO;
using System.Net.Mime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EnhancedPdfReader.Api;

/// <summary>
/// Serves the Enhanced PDF Reader client assets.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("EnhancedPdfReader")]
public class EnhancedPdfReaderController : ControllerBase
{
    private readonly ILogger<EnhancedPdfReaderController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EnhancedPdfReaderController"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{EnhancedPdfReaderController}"/> interface.</param>
    public EnhancedPdfReaderController(ILogger<EnhancedPdfReaderController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the client bootstrap script.
    /// </summary>
    /// <returns>The JavaScript file.</returns>
    [HttpGet("ClientScript")]
    public ActionResult GetClientScript()
    {
        _logger.LogInformation("[EnhancedPdfReader] client script requested (v={Version})", Request.Query["v"].ToString());
        return Serve("Web.enhancedPdfReader.js", "text/javascript");
    }

    /// <summary>
    /// Gets the pdf.js library.
    /// </summary>
    /// <returns>The pdf.js module.</returns>
    [HttpGet("pdf.mjs")]
    public ActionResult GetPdfLib()
        => Serve("Web.pdf.min.mjs", "text/javascript");

    /// <summary>
    /// Gets the pdf.js worker.
    /// </summary>
    /// <returns>The pdf.js worker module.</returns>
    [HttpGet("pdf.worker.mjs")]
    public ActionResult GetPdfWorker()
        => Serve("Web.pdf.worker.min.mjs", "text/javascript");

    /// <summary>
    /// Gets the StPageFlip library used by the book (page turning) mode.
    /// </summary>
    /// <returns>The page-flip browser bundle.</returns>
    [HttpGet("page-flip.js")]
    public ActionResult GetPageFlip()
        => Serve("Web.page-flip.browser.js", "text/javascript");

    private ActionResult Serve(string relativeName, string contentType)
    {
        var assembly = typeof(EnhancedPdfReaderController).Assembly;
        var resource = typeof(Plugin).Namespace + "." + relativeName;
        var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return NotFound(resource);
        }

        // never let a browser sit on an old copy of the reader
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        Response.Headers.Pragma = "no-cache";
        return File(stream, contentType);
    }
}

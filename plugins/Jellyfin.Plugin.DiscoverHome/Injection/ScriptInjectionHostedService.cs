using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DiscoverHome.Injection;

/// <summary>
/// Adds the DiscoverHome script tag to the web client's index.html on startup.
/// </summary>
/// <remarks>
/// Jellyfin has no supported API for extending the web client, so the script tag is
/// added to index.html. The edit is idempotent and additive: it inserts one tag and
/// changes nothing else, so a failed or reverted injection costs the styling and never
/// the web client itself.
/// </remarks>
public class ScriptInjectionHostedService : IHostedService
{
    private const string Marker = "DiscoverHome/ClientScript";
    private const string ScriptTag =
        "<script defer=\"defer\" src=\"../DiscoverHome/ClientScript?v=0.1.0\"></script>";

    private readonly IServerApplicationPaths _paths;
    private readonly ILogger<ScriptInjectionHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptInjectionHostedService"/> class.
    /// </summary>
    /// <param name="paths">Server application paths.</param>
    /// <param name="logger">Logger.</param>
    public ScriptInjectionHostedService(
        IServerApplicationPaths paths,
        ILogger<ScriptInjectionHostedService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var webPath = _paths.WebPath;
            if (string.IsNullOrEmpty(webPath))
            {
                _logger.LogWarning("DiscoverHome: web path is empty; cannot add the client script.");
                return Task.CompletedTask;
            }

            var indexFile = Path.Combine(webPath, "index.html");
            if (!File.Exists(indexFile))
            {
                _logger.LogWarning("DiscoverHome: {IndexFile} not found.", indexFile);
                return Task.CompletedTask;
            }

            var html = File.ReadAllText(indexFile);
            var present = html.Contains(Marker, StringComparison.Ordinal);

            if (!Plugin.Config.InjectClientScript)
            {
                // Turning the setting off has to actually remove the tag, otherwise
                // "disabled" would still load and run the script on every page.
                if (present)
                {
                    var stripped = Regex.Replace(
                        html,
                        "<script[^>]+src=[\"'][^\"']*DiscoverHome/ClientScript[^\"']*[\"'][^>]*></script>",
                        string.Empty,
                        RegexOptions.IgnoreCase);
                    File.WriteAllText(indexFile, stripped);
                    _logger.LogInformation("DiscoverHome: removed the client script from {IndexFile}.", indexFile);
                }

                return Task.CompletedTask;
            }

            if (present)
            {
                // Refresh the tag so the cache-busting version follows plugin upgrades.
                var updated = Regex.Replace(
                    html,
                    "<script[^>]+src=[\"'][^\"']*DiscoverHome/ClientScript[^\"']*[\"'][^>]*></script>",
                    ScriptTag,
                    RegexOptions.IgnoreCase);

                if (!string.Equals(html, updated, StringComparison.Ordinal))
                {
                    File.WriteAllText(indexFile, updated);
                }

                return Task.CompletedTask;
            }

            var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                _logger.LogWarning("DiscoverHome: could not find </body> in index.html.");
                return Task.CompletedTask;
            }

            File.WriteAllText(indexFile, html.Insert(idx, ScriptTag));
            _logger.LogInformation("DiscoverHome: added the client script to {IndexFile}.", indexFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only web root is a supported deployment, not a fatal error.
            _logger.LogError(ex, "DiscoverHome: could not modify the web client; the layer will not load.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

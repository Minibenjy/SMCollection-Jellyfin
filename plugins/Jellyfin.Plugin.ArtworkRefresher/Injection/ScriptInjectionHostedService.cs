using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArtworkRefresher.Injection;

/// <summary>
/// Injects the topbar switch client script into the web client's index.html on startup.
/// </summary>
public class ScriptInjectionHostedService : IHostedService
{
    private const string Marker = "ArtworkRefresher/ClientScript";
    private const string ScriptTag = "<script defer=\"defer\" src=\"../ArtworkRefresher/ClientScript?v=1.0.0\"></script>";

    private readonly IServerApplicationPaths _paths;
    private readonly ILogger<ScriptInjectionHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptInjectionHostedService"/> class.
    /// </summary>
    /// <param name="paths">Server application paths.</param>
    /// <param name="logger">Logger.</param>
    public ScriptInjectionHostedService(IServerApplicationPaths paths, ILogger<ScriptInjectionHostedService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!Plugin.Config.InjectClientScript)
            {
                _logger.LogInformation("Artwork Refresher: client script injection disabled in config.");
                return Task.CompletedTask;
            }

            var webPath = _paths.WebPath;
            if (string.IsNullOrEmpty(webPath))
            {
                _logger.LogWarning("Artwork Refresher: web path is empty, cannot inject client script.");
                return Task.CompletedTask;
            }

            var indexFile = Path.Combine(webPath, "index.html");
            if (!File.Exists(indexFile))
            {
                _logger.LogWarning("Artwork Refresher: {IndexFile} not found.", indexFile);
                return Task.CompletedTask;
            }

            var html = File.ReadAllText(indexFile);
            if (html.Contains(Marker, StringComparison.Ordinal))
            {
                var updated = Regex.Replace(
                    html,
                    "<script[^>]+src=[\"'][^\"']*ArtworkRefresher/ClientScript[^\"']*[\"'][^>]*></script>",
                    ScriptTag,
                    RegexOptions.IgnoreCase);
                if (!string.Equals(html, updated, StringComparison.Ordinal))
                {
                    File.WriteAllText(indexFile, updated);
                    _logger.LogInformation("Artwork Refresher: refreshed client script tag in {IndexFile}.", indexFile);
                }

                return Task.CompletedTask;
            }

            var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                _logger.LogWarning("Artwork Refresher: could not find </body> in index.html.");
                return Task.CompletedTask;
            }

            html = html.Insert(idx, ScriptTag);
            File.WriteAllText(indexFile, html);
            _logger.LogInformation("Artwork Refresher: injected client script into {IndexFile}.", indexFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Artwork Refresher: failed to inject client script.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

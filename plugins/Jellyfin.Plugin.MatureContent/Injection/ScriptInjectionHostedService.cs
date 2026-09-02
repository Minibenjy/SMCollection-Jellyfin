using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MatureContent.Injection;

/// <summary>
/// Injects the topbar toggle client script into the web client's index.html on startup.
/// </summary>
public class ScriptInjectionHostedService : IHostedService
{
    private const string Marker = "MatureContent/ClientScript";
    private const string ScriptTag = "<script defer=\"defer\" src=\"../MatureContent/ClientScript?v=0.1.6\"></script>";

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
                _logger.LogInformation("Mature Content: client script injection disabled in config.");
                return Task.CompletedTask;
            }

            var webPath = _paths.WebPath;
            if (string.IsNullOrEmpty(webPath))
            {
                _logger.LogWarning("Mature Content: web path is empty, cannot inject client script.");
                return Task.CompletedTask;
            }

            var indexFile = Path.Combine(webPath, "index.html");
            if (!File.Exists(indexFile))
            {
                _logger.LogWarning("Mature Content: {IndexFile} not found.", indexFile);
                return Task.CompletedTask;
            }

            var html = File.ReadAllText(indexFile);
            if (html.Contains(Marker, StringComparison.Ordinal))
            {
                var updated = Regex.Replace(
                    html,
                    "<script[^>]+src=[\"'][^\"']*MatureContent/ClientScript[^\"']*[\"'][^>]*></script>",
                    ScriptTag,
                    RegexOptions.IgnoreCase);
                if (!string.Equals(html, updated, StringComparison.Ordinal))
                {
                    File.WriteAllText(indexFile, updated);
                    _logger.LogInformation("Mature Content: refreshed client script tag in {IndexFile}.", indexFile);
                }
                else
                {
                    _logger.LogDebug("Mature Content: client script already injected.");
                }

                return Task.CompletedTask;
            }

            var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                _logger.LogWarning("Mature Content: could not find </body> in index.html.");
                return Task.CompletedTask;
            }

            html = html.Insert(idx, ScriptTag);
            File.WriteAllText(indexFile, html);
            _logger.LogInformation("Mature Content: injected client script into {IndexFile}.", indexFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mature Content: failed to inject client script.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

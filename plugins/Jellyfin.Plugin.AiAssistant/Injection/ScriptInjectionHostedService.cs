using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiAssistant.Injection;

/// <summary>
/// Adds the assistant's script tag to the web client's index.html on startup.
/// </summary>
/// <remarks>
/// Jellyfin has no supported API for extending the web client, so the script tag is
/// added to index.html. The edit is idempotent and additive: it inserts one tag and
/// changes nothing else, so a failed or reverted injection costs the launcher button
/// and never the web client itself.
/// </remarks>
public class ScriptInjectionHostedService : IHostedService
{
    private const string Marker = "AiAssistant/ClientScript";
    private const string ScriptTag =
        "<script defer=\"defer\" src=\"../AiAssistant/ClientScript?v=0.1.0\"></script>";

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
            if (!Plugin.Config.InjectClientScript)
            {
                return Task.CompletedTask;
            }

            var webPath = _paths.WebPath;
            if (string.IsNullOrEmpty(webPath))
            {
                _logger.LogWarning("AI Assistant: web path is empty; cannot add the client script.");
                return Task.CompletedTask;
            }

            var indexFile = Path.Combine(webPath, "index.html");
            if (!File.Exists(indexFile))
            {
                _logger.LogWarning("AI Assistant: {IndexFile} not found.", indexFile);
                return Task.CompletedTask;
            }

            var html = File.ReadAllText(indexFile);

            if (html.Contains(Marker, StringComparison.Ordinal))
            {
                // Refresh the tag so the cache-busting version follows plugin upgrades.
                var updated = Regex.Replace(
                    html,
                    "<script[^>]+src=[\"'][^\"']*AiAssistant/ClientScript[^\"']*[\"'][^>]*></script>",
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
                _logger.LogWarning("AI Assistant: could not find </body> in index.html.");
                return Task.CompletedTask;
            }

            File.WriteAllText(indexFile, html.Insert(idx, ScriptTag));
            _logger.LogInformation("AI Assistant: added the client script to {IndexFile}.", indexFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only web root is a supported deployment, not a fatal error.
            _logger.LogError(ex, "AI Assistant: could not modify the web client; the launcher will not appear.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

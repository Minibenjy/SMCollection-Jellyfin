using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EnhancedPdfReader.Injection;

/// <summary>
/// Injects the client script tag into the web client's index.html on startup.
/// </summary>
public class ScriptInjectionHostedService : IHostedService
{
    private const string Marker = "EnhancedPdfReader/ClientScript";
    private const string ScriptVersion = "1.3.0";
    private const string ScriptTag = "<script defer=\"defer\" src=\"../EnhancedPdfReader/ClientScript?v=" + ScriptVersion + "\"></script>";

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
            if (!(Plugin.Instance?.Configuration.InjectClientScript ?? true))
            {
                _logger.LogInformation("Enhanced PDF Reader: client script injection disabled in config.");
                return Task.CompletedTask;
            }

            var webPath = _paths.WebPath;
            if (string.IsNullOrEmpty(webPath))
            {
                _logger.LogWarning("Enhanced PDF Reader: web path is empty, cannot inject client script.");
                return Task.CompletedTask;
            }

            var indexFile = Path.Combine(webPath, "index.html");
            if (!File.Exists(indexFile))
            {
                _logger.LogWarning("Enhanced PDF Reader: {IndexFile} not found.", indexFile);
                return Task.CompletedTask;
            }

            var html = File.ReadAllText(indexFile);
            if (html.Contains(ScriptTag, StringComparison.Ordinal))
            {
                _logger.LogDebug("Enhanced PDF Reader: client script already injected.");
                return Task.CompletedTask;
            }

            if (html.Contains(Marker, StringComparison.Ordinal))
            {
                // an older version of the tag is there: swap it so browsers refetch the script
                html = System.Text.RegularExpressions.Regex.Replace(
                    html,
                    "<script[^>]*" + Marker + "[^>]*></script>",
                    ScriptTag);
                File.WriteAllText(indexFile, html);
                _logger.LogInformation("Enhanced PDF Reader: updated client script tag to v{Version}.", ScriptVersion);
                return Task.CompletedTask;
            }

            var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                _logger.LogWarning("Enhanced PDF Reader: could not find </body> in index.html.");
                return Task.CompletedTask;
            }

            html = html.Insert(idx, ScriptTag);
            File.WriteAllText(indexFile, html);
            _logger.LogInformation("Enhanced PDF Reader: injected client script into {IndexFile}.", indexFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Enhanced PDF Reader: failed to inject client script.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

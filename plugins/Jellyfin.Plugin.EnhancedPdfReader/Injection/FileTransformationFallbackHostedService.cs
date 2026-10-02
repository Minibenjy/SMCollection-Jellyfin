using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EnhancedPdfReader.Injection;

/// <summary>
/// Fallback for servers where jellyfin-web is read-only (typical in containers running as a
/// non-root user): instead of editing index.html on disk, ask the File Transformation plugin,
/// when it is installed, to add the script tag as the page is served. Does nothing when the
/// tag is already in index.html or File Transformation is absent.
/// </summary>
public class FileTransformationFallbackHostedService : IHostedService
{
    private const string Marker = "EnhancedPdfReader/ClientScript";

    private readonly IServerApplicationPaths _paths;
    private readonly ILogger<FileTransformationFallbackHostedService> _logger;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileTransformationFallbackHostedService"/> class.
    /// </summary>
    /// <param name="paths">Server application paths.</param>
    /// <param name="logger">Logger.</param>
    public FileTransformationFallbackHostedService(IServerApplicationPaths paths, ILogger<FileTransformationFallbackHostedService> logger)
    {
        _paths = paths;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RegisterWhenReadyAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Called by File Transformation for index.html. Public and static because it is resolved by reflection.
    /// </summary>
    /// <param name="payload">The request payload carrying the file contents.</param>
    /// <returns>The contents with the script tag added.</returns>
    public static string Patch(PatchRequestPayload payload)
    {
        var html = payload.Contents ?? string.Empty;
        if (html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        var idx = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return html;
        }

        var version = typeof(FileTransformationFallbackHostedService).Assembly.GetName().Version?.ToString() ?? "0";
        return html.Insert(idx, "<script defer=\"defer\" src=\"../EnhancedPdfReader/ClientScript?v=" + version + "\"></script>");
    }

    private static bool Enabled()
    {
        return Plugin.Instance?.Configuration.InjectClientScript ?? true;
    }

    private async Task RegisterWhenReadyAsync(CancellationToken token)
    {
        try
        {
            if (!Enabled())
            {
                return;
            }

            // Give the on-disk injector the first go; only step in if it did not leave the tag.
            await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            if (IndexAlreadyInjected())
            {
                _logger.LogInformation("EnhancedPdfReader: client script already in index.html, File Transformation fallback not needed.");
                return;
            }

            for (var attempt = 0; attempt < 24 && !token.IsCancellationRequested; attempt++)
            {
                if (TryRegister())
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            }

            _logger.LogInformation("EnhancedPdfReader: File Transformation plugin not found; client script is not injected (install it, make jellyfin-web writable, or use a JavaScript injector).");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EnhancedPdfReader: File Transformation fallback failed.");
        }
    }

    private bool IndexAlreadyInjected()
    {
        try
        {
            var webPath = _paths.WebPath;
            var index = string.IsNullOrEmpty(webPath) ? null : System.IO.Path.Combine(webPath, "index.html");
            return index is not null && System.IO.File.Exists(index)
                && System.IO.File.ReadAllText(index).Contains(Marker, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool TryRegister()
    {
        var ftAssembly = AssemblyLoadContext.All
            .SelectMany(c => c.Assemblies)
            .FirstOrDefault(a => a.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);
        var iface = ftAssembly?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        var method = iface?.GetMethod("RegisterTransformation");
        if (method is null)
        {
            return false;
        }

        // The payload is a Newtonsoft JObject living in File Transformation's own context, so build it
        // through the parameter type instead of referencing Newtonsoft here.
        var payloadType = method.GetParameters()[0].ParameterType;
        var json = "{\"id\":\"2d5a7671-c580-5d8f-8276-19fa01469519\",\"fileNamePattern\":\"index\\\\.html\",\"callbackAssembly\":\""
            + typeof(FileTransformationFallbackHostedService).Assembly.FullName + "\",\"callbackClass\":\""
            + typeof(FileTransformationFallbackHostedService).FullName + "\",\"callbackMethod\":\"Patch\"}";
        var parse = payloadType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
        var payload = parse?.Invoke(null, new object[] { json });
        if (payload is null)
        {
            return false;
        }

        method.Invoke(null, new[] { payload });
        _logger.LogInformation("EnhancedPdfReader: registered the client script with File Transformation.");
        return true;
    }
}

/// <summary>
/// Shape File Transformation deserialises its payload into.
/// </summary>
public class PatchRequestPayload
{
    /// <summary>
    /// Gets or sets the current contents of the requested file.
    /// </summary>
    public string? Contents { get; set; }
}

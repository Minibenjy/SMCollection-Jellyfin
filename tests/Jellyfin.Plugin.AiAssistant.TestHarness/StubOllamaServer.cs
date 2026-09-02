using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AiAssistant.TestHarness;

/// <summary>
/// A minimal stand-in for a real Ollama instance.
/// </summary>
/// <remarks>
/// The wire format is the part of the provider most likely to be wrong and the part
/// a compiler cannot check, so it is exercised against canned responses rather than
/// against a live model. That keeps the check deterministic and runnable in CI,
/// where no Ollama exists.
/// </remarks>
public sealed class StubOllamaServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Gets or sets the body returned from /api/chat.</summary>
    public string ChatResponse { get; set; } = "{}";

    /// <summary>Gets or sets the body returned from /api/tags.</summary>
    public string TagsResponse { get; set; } = "{\"models\":[]}";

    /// <summary>Gets the body of the last request received.</summary>
    public string? LastRequestBody { get; private set; }

    /// <summary>Gets the base address the stub is listening on.</summary>
    public string BaseUrl { get; }

    /// <summary>Initializes a new instance of the <see cref="StubOllamaServer"/> class.</summary>
    /// <param name="port">Port to listen on.</param>
    public StubOllamaServer(int port)
    {
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            using (var reader = new StreamReader(context.Request.InputStream))
            {
                LastRequestBody = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            var body = path.Contains("tags", StringComparison.Ordinal) ? TagsResponse : ChatResponse;
            var bytes = Encoding.UTF8.GetBytes(body);

            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        _cts.Dispose();
    }
}

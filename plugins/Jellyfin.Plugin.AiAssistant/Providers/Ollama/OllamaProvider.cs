using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiAssistant.Providers.Ollama;

/// <summary>
/// Provider for a self-hosted Ollama instance.
/// </summary>
/// <remarks>
/// Ollama needs no credential and keeps every request on the operator's own
/// hardware, which makes it the privacy-preserving default for this plugin.
/// </remarks>
public sealed class OllamaProvider : IChatProvider
{
    private const string DefaultBaseUrl = "http://localhost:11434";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OllamaProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OllamaProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public OllamaProvider(IHttpClientFactory httpClientFactory, ILogger<OllamaProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => "ollama";

    /// <inheritdoc />
    public string DisplayName => "Ollama (self-hosted)";

    /// <inheritdoc />
    public bool RequiresCredential => false;

    /// <inheritdoc />
    public bool SupportsTools => true;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListModelsAsync(ProviderConnection connection, CancellationToken cancellationToken)
    {
        using var client = CreateClient(connection);

        using var response = await client.GetAsync(new Uri("api/tags", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false);
        var models = payload?["models"]?.AsArray();
        if (models is null)
        {
            return Array.Empty<string>();
        }

        return models
            .Select(m => m?["name"]?.GetValue<string>())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ChatResponse> CompleteAsync(ProviderConnection connection, ChatRequest request, CancellationToken cancellationToken)
    {
        using var client = CreateClient(connection);

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["stream"] = false,
            ["messages"] = BuildMessages(request),
            ["options"] = new JsonObject
            {
                ["num_predict"] = request.MaxTokens
            }
        };

        if (request.Tools.Count > 0)
        {
            body["tools"] = BuildTools(request.Tools);
        }

        using var response = await client.PostAsJsonAsync(new Uri("api/chat", UriKind.Relative), body, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Ollama returned {Status}: {Detail}", (int)response.StatusCode, detail);
            throw new ProviderException(
                string.Create(CultureInfo.InvariantCulture, $"Ollama returned {(int)response.StatusCode}."));
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false)
                      ?? throw new ProviderException("Ollama returned an empty response.");

        return Translate(payload, request);
    }

    private static JsonArray BuildMessages(ChatRequest request)
    {
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = request.SystemPrompt
            }
        };

        foreach (var message in request.Messages)
        {
            // Tool results are their own Ollama turns, one per result.
            if (message.ToolResults.Count > 0)
            {
                foreach (var result in message.ToolResults)
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["content"] = result.Content
                    });
                }

                continue;
            }

            var entry = new JsonObject
            {
                ["role"] = message.Role switch
                {
                    ChatRole.User => "user",
                    ChatRole.Assistant => "assistant",
                    ChatRole.System => "system",
                    _ => "user"
                },
                ["content"] = message.Text ?? string.Empty
            };

            if (message.ToolCalls.Count > 0)
            {
                var calls = new JsonArray();
                foreach (var call in message.ToolCalls)
                {
                    calls.Add(new JsonObject
                    {
                        ["function"] = new JsonObject
                        {
                            ["name"] = call.Name,
                            ["arguments"] = call.Arguments.DeepClone()
                        }
                    });
                }

                entry["tool_calls"] = calls;
            }

            messages.Add(entry);
        }

        return messages;
    }

    private static JsonArray BuildTools(IEnumerable<ChatToolDefinition> tools)
    {
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            array.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.ParametersSchema.DeepClone()
                }
            });
        }

        return array;
    }

    private static ChatResponse Translate(JsonNode payload, ChatRequest request)
    {
        var message = payload["message"];
        var result = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Text = message?["content"]?.GetValue<string>()
        };

        var toolCalls = message?["tool_calls"]?.AsArray();
        if (toolCalls is not null)
        {
            var index = 0;
            foreach (var call in toolCalls)
            {
                var function = call?["function"];
                var name = function?["name"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                // Ollama does not assign correlation ids to tool calls, so synthesize
                // stable ones for this turn to keep the neutral model uniform.
                var id = string.Create(CultureInfo.InvariantCulture, $"call_{index++}");
                var arguments = function?["arguments"] as JsonObject
                                ?? ParseArguments(function?["arguments"]);

                result.ToolCalls.Add(new ChatToolCall(id, name, arguments));
            }
        }

        // Structured tool calling is preferred and used whenever it arrives. When it
        // does not, the call is often sitting in the reply text instead; recovering it
        // there costs nothing and turns a wall of JSON shown to the user into the
        // action they asked for.
        if (result.ToolCalls.Count == 0
            && ToolCallSalvage.TryRecover(
                result.Text,
                request.Tools.Select(t => t.Name).ToList(),
                out var salvaged,
                out var remaining)
            && salvaged is not null)
        {
            result.ToolCalls.Add(salvaged);
            result.Text = remaining;
        }

        var stop = result.ToolCalls.Count > 0
            ? ChatStopReason.ToolUse
            : payload["done_reason"]?.GetValue<string>() switch
            {
                "length" => ChatStopReason.MaxTokens,
                _ => ChatStopReason.EndTurn
            };

        return new ChatResponse(result, stop);
    }

    /// <summary>
    /// Recovers arguments from models that emit them as a JSON string rather than an object.
    /// </summary>
    private static JsonObject ParseArguments(JsonNode? node)
    {
        if (node is null)
        {
            return new JsonObject();
        }

        try
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var raw))
            {
                return JsonNode.Parse(raw) as JsonObject ?? new JsonObject();
            }
        }
        catch (JsonException)
        {
            // Fall through to an empty argument set; the tool reports the validation error.
        }

        return new JsonObject();
    }

    private HttpClient CreateClient(ProviderConnection connection)
    {
        var client = _httpClientFactory.CreateClient(nameof(OllamaProvider));
        var baseUrl = string.IsNullOrWhiteSpace(connection.BaseUrl) ? DefaultBaseUrl : connection.BaseUrl;
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromMinutes(5);
        return client;
    }
}

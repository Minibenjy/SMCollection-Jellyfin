using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AiAssistant.Guardrails;
using Jellyfin.Plugin.AiAssistant.Providers;
using Jellyfin.Plugin.AiAssistant.Tools;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AiAssistant.Assistant;

/// <summary>
/// Runs one exchange: model, tools, model again, until the assistant answers.
/// </summary>
public sealed class ConversationService
{
    private readonly ProviderResolver _resolver;
    private readonly ToolRegistry _tools;
    private readonly RateLimiter _rateLimiter;
    private readonly Configuration.MetadataLanguageResolver _metadataLanguage;
    private readonly ILogger<ConversationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationService"/> class.
    /// </summary>
    /// <param name="resolver">Provider resolver.</param>
    /// <param name="tools">Tool registry.</param>
    /// <param name="rateLimiter">Rate limiter.</param>
    /// <param name="metadataLanguage">Metadata language resolver.</param>
    /// <param name="logger">Logger.</param>
    public ConversationService(
        ProviderResolver resolver,
        ToolRegistry tools,
        RateLimiter rateLimiter,
        Configuration.MetadataLanguageResolver metadataLanguage,
        ILogger<ConversationService> logger)
    {
        _resolver = resolver;
        _tools = tools;
        _rateLimiter = rateLimiter;
        _metadataLanguage = metadataLanguage;
        _logger = logger;
    }

    /// <summary>
    /// Answers one user message.
    /// </summary>
    /// <param name="scope">The acting user. All tool calls run inside it.</param>
    /// <param name="history">Prior turns of this conversation.</param>
    /// <param name="userMessage">The new user message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assistant's reply.</returns>
    public async Task<ExchangeResult> AskAsync(
        UserScope scope,
        IReadOnlyList<ChatMessage> history,
        string userMessage,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Config;

        if (!_rateLimiter.TryAcquire(scope.UserId, config.MaxRequestsPerUserPerHour))
        {
            return ExchangeResult.Failed(
                "You have reached the assistant's hourly limit on this server. Try again later.");
        }

        ResolvedProvider route;
        try
        {
            route = await _resolver.ResolveAsync(scope.UserId, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException ex)
        {
            return ExchangeResult.Failed(ex.Message);
        }

        var allowMutating = config.EnableMutatingTools;

        // A provider that cannot call tools gets none, and the system prompt tells the
        // model to say it cannot check rather than answer from its training data.
        var toolDefinitions = route.Provider.SupportsTools
            ? _tools.Describe(allowMutating)
            : Array.Empty<ChatToolDefinition>();

        var request = new ChatRequest
        {
            Model = route.Model,
            SystemPrompt = SystemPrompt.Build(
                scope.User.Username,
                toolDefinitions.Select(t => t.Name).ToList(),
                config.ServerLabel,
                _metadataLanguage.Resolve(scope.UserId)),
        };

        foreach (var message in history)
        {
            request.Messages.Add(message);
        }

        request.Messages.Add(new ChatMessage { Role = ChatRole.User, Text = userMessage });

        foreach (var tool in toolDefinitions)
        {
            request.Tools.Add(tool);
        }

        return await RunLoopAsync(scope, route, request, allowMutating, 0, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes an exchange after the user answered a confirmation prompt.
    /// </summary>
    /// <param name="scope">The acting user.</param>
    /// <param name="pending">The action they were asked about.</param>
    /// <param name="approved">Whether they approved it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assistant's reply.</returns>
    public async Task<ExchangeResult> ResolveAsync(
        UserScope scope,
        PendingAction pending,
        bool approved,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Config;

        ResolvedProvider route;
        try
        {
            route = await _resolver.ResolveAsync(scope.UserId, cancellationToken).ConfigureAwait(false);
        }
        catch (ProviderException ex)
        {
            return ExchangeResult.Failed(ex.Message);
        }

        var request = RebuildRequest(scope, route, config, pending.Transcript);

        ChatToolResult result;
        if (!approved)
        {
            // The model is told plainly, so it acknowledges the refusal instead of
            // reporting the action as done.
            result = new ChatToolResult(
                pending.CallId,
                JsonSerializer.Serialize(new { declined = true, reason = "The user declined this action." }));
        }
        else
        {
            result = await RunToolAsync(
                    scope,
                    new ChatToolCall(pending.CallId, pending.ToolName, pending.Arguments),
                    config.EnableMutatingTools,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var results = new ChatMessage { Role = ChatRole.Tool };
        results.ToolResults.Add(result);
        request.Messages.Add(results);

        return await RunLoopAsync(
            scope, route, request, config.EnableMutatingTools, pending.ToolCallsUsed, cancellationToken)
            .ConfigureAwait(false);
    }

    private ChatRequest RebuildRequest(
        UserScope scope,
        ResolvedProvider route,
        Configuration.PluginConfiguration config,
        IReadOnlyList<ChatMessage> transcript)
    {
        var toolDefinitions = route.Provider.SupportsTools
            ? _tools.Describe(config.EnableMutatingTools)
            : Array.Empty<ChatToolDefinition>();

        var request = new ChatRequest
        {
            Model = route.Model,
            SystemPrompt = SystemPrompt.Build(
                scope.User.Username,
                toolDefinitions.Select(t => t.Name).ToList(),
                config.ServerLabel,
                _metadataLanguage.Resolve(scope.UserId))
        };

        foreach (var message in transcript)
        {
            request.Messages.Add(message);
        }

        foreach (var tool in toolDefinitions)
        {
            request.Tools.Add(tool);
        }

        return request;
    }

    private async Task<ExchangeResult> RunLoopAsync(
        UserScope scope,
        ResolvedProvider route,
        ChatRequest request,
        bool allowMutating,
        int toolCallsUsed,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Config;
        var toolCallBudget = Math.Max(1, config.MaxToolCallsPerExchange);

        // What has already been tried this exchange, so an identical call can be
        // answered instead of run. See RunToolAsync.
        var attempted = new Dictionary<string, string>(StringComparer.Ordinal);

        while (true)
        {
            ChatResponse response;
            try
            {
                response = await route.Provider
                    .CompleteAsync(route.Connection, request, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ProviderException ex)
            {
                return ExchangeResult.Failed(ex.Message);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Provider {Provider} was unreachable during an exchange.", route.Provider.Id);

                // Jellyfin frequently runs in a container, where "localhost" is the
                // container itself rather than the machine the user has in mind. That
                // is the single most common cause of this failure, so say it.
                var endpoint = route.Connection.BaseUrl;
                var looksLocal = string.IsNullOrWhiteSpace(endpoint)
                                 || endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                                 || endpoint.Contains("127.0.0.1", StringComparison.Ordinal);

                return ExchangeResult.Failed(looksLocal
                    ? "Could not reach the AI provider. If it runs on another machine, set its address in your assistant settings — this server cannot reach it through \"localhost\"."
                    : "Could not reach the AI provider at the address in your assistant settings. Check that it is running and accepting connections from this server.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The upstream failure text may contain the endpoint or key material,
                // so it goes to the log and never to the user.
                _logger.LogError(ex, "Provider {Provider} failed during an exchange.", route.Provider.Id);
                return ExchangeResult.Failed("The AI provider returned something this plugin could not read.");
            }

            request.Messages.Add(response.Message);

            if (response.StopReason != ChatStopReason.ToolUse || response.Message.ToolCalls.Count == 0)
            {
                return ExchangeResult.Answered(response.Message.Text ?? string.Empty, request.Messages.ToList());
            }

            if (toolCallsUsed + response.Message.ToolCalls.Count > toolCallBudget)
            {
                _logger.LogWarning(
                    "Exchange for user {UserId} hit the tool call budget of {Budget}.",
                    scope.UserId,
                    toolCallBudget);

                return ExchangeResult.Answered(
                    "I had to stop before finishing that — it needed more lookups than this server allows in one go. Try asking something narrower.",
                    request.Messages.ToList());
            }

            // A write is proposed, never performed. The exchange stops here and resumes
            // in ResolveAsync once the person has answered.
            foreach (var call in response.Message.ToolCalls)
            {
                var candidate = _tools.Resolve(call.Name, allowMutating);
                if (candidate is { IsMutating: true })
                {
                    return ExchangeResult.NeedsConfirmation(new PendingAction(
                        call.Id,
                        call.Name,
                        call.Arguments,
                        _tools.Describe(candidate, scope, call.Arguments),
                        request.Messages.ToList(),
                        toolCallsUsed));
                }
            }

            var results = new ChatMessage { Role = ChatRole.Tool };
            foreach (var call in response.Message.ToolCalls)
            {
                toolCallsUsed++;
                results.ToolResults.Add(
                    await RunToolAsync(scope, call, allowMutating, attempted, cancellationToken).ConfigureAwait(false));
            }

            request.Messages.Add(results);
        }
    }

    /// <remarks>
    /// <paramref name="attempted"/> is what makes a stuck model unstick. Observed
    /// live: a search that came back empty was reissued with byte-identical arguments
    /// on the very next turn, and the exchange burned its budget repeating itself.
    /// Returning the previous answer plus an instruction to change approach costs one
    /// turn instead of all of them, and is a mechanism rather than a request the model
    /// is free to ignore.
    /// </remarks>
    private async Task<ChatToolResult> RunToolAsync(
        UserScope scope,
        ChatToolCall call,
        bool allowMutating,
        Dictionary<string, string> attempted,
        CancellationToken cancellationToken)
    {
        var signature = call.Name + "|" + call.Arguments.ToJsonString();
        if (attempted.TryGetValue(signature, out var previous))
        {
            _logger.LogInformation(
                "Tool {Tool} was called twice with identical arguments in one exchange.",
                call.Name);

            return new ChatToolResult(
                call.Id,
                JsonSerializer.Serialize(new
                {
                    repeated_call = true,
                    error = "You already made this exact call in this exchange and this is what it "
                            + "returned. Repeating it will not change the answer. Either use a "
                            + "different tool or different arguments, or tell the user what you "
                            + "found and what you could not find.",
                    previous_result = previous
                }),
                IsError: true);
        }

        var tool = _tools.Resolve(call.Name, allowMutating);
        if (tool is null)
        {
            // Either a hallucinated name or an attempt to reach a disabled capability.
            // The model is told plainly so it can recover, and nothing runs.
            return new ChatToolResult(
                call.Id,
                JsonSerializer.Serialize(new
                {
                    error = "No such tool is available.",
                    available_tools = _tools.Describe(allowMutating).Select(t => t.Name).ToArray()
                }),
                IsError: true);
        }

        try
        {
            var result = await tool.ExecuteAsync(scope, call.Arguments, cancellationToken).ConfigureAwait(false);
            var payload = result.ToJsonString();

            // Arguments and result size, not the result itself: enough to tell a bad
            // tool call from a bad summary of a good one, without copying the user's
            // library contents into the server log.
            _logger.LogInformation(
                "Tool {Tool} called with {Arguments} returned {Bytes} bytes.",
                call.Name,
                call.Arguments.ToJsonString(),
                payload.Length);

            attempted[signature] = payload.Length > 2000 ? payload[..2000] + "…" : payload;

            return new ChatToolResult(call.Id, payload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Tool {Tool} failed for user {UserId}.", call.Name, scope.UserId);

            // The model cannot see the exception, so a bare "it failed" invites it to
            // repeat the same malformed call until the budget is gone. Naming the
            // argument shape gives it something to correct.
            return new ChatToolResult(
                call.Id,
                JsonSerializer.Serialize(new
                {
                    error = "The tool could not run with those arguments. Check them against the tool's schema and try once more, or tell the user you could not complete the lookup.",
                    tool = call.Name
                }),
                IsError: true);
        }
    }
}

/// <summary>
/// The outcome of one exchange.
/// </summary>
/// <param name="Reply">Text to show the user.</param>
/// <param name="Success">Whether the assistant answered.</param>
/// <param name="History">The conversation to carry into the next turn.</param>
/// <param name="Pending">A write awaiting the user's approval, when there is one.</param>
public record ExchangeResult(
    string Reply,
    bool Success,
    IReadOnlyList<ChatMessage> History,
    PendingAction? Pending = null)
{
    /// <summary>Creates a successful result.</summary>
    /// <param name="reply">The assistant's answer.</param>
    /// <param name="history">The updated conversation.</param>
    /// <returns>The result.</returns>
    public static ExchangeResult Answered(string reply, IReadOnlyList<ChatMessage> history)
        => new(reply, true, history);

    /// <summary>Creates a result awaiting the user's approval of a write.</summary>
    /// <param name="pending">The action to confirm.</param>
    /// <returns>The result.</returns>
    public static ExchangeResult NeedsConfirmation(PendingAction pending)
        => new(pending.Description, true, Array.Empty<ChatMessage>(), pending);

    /// <summary>Creates a failed result.</summary>
    /// <param name="reason">A user-safe explanation.</param>
    /// <returns>The result.</returns>
    public static ExchangeResult Failed(string reason)
        => new(reason, false, Array.Empty<ChatMessage>());
}

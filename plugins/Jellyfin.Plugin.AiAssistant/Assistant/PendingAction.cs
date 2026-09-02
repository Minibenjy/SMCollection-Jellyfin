using System.Text.Json.Nodes;
using Jellyfin.Plugin.AiAssistant.Providers;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AiAssistant.Assistant;

/// <summary>
/// A state-changing tool call waiting for the user to approve it.
/// </summary>
/// <remarks>
/// Nothing that writes runs on the model's say-so. The call is captured here with
/// the conversation that produced it, described in plain language, and executed only
/// after the person says yes — OWASP LLM06's human-in-the-loop control for
/// high-impact actions, and the reason the assistant cannot quietly fill someone's
/// account with things they did not ask for.
/// </remarks>
/// <param name="CallId">Correlation id the model used.</param>
/// <param name="ToolName">Tool the model wants to run.</param>
/// <param name="Arguments">Arguments it supplied.</param>
/// <param name="Description">One line shown to the user.</param>
/// <param name="Transcript">Conversation to resume once answered.</param>
/// <param name="ToolCallsUsed">Tool calls already spent this exchange.</param>
public record PendingAction(
    string CallId,
    string ToolName,
    JsonObject Arguments,
    string Description,
    IReadOnlyList<ChatMessage> Transcript,
    int ToolCallsUsed);

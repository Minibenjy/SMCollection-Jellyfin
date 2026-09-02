using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.AiAssistant.Providers;

/// <summary>
/// Role of a message in a conversation, normalized across providers.
/// </summary>
public enum ChatRole
{
    /// <summary>Operator instructions. Never sourced from user input.</summary>
    System,

    /// <summary>Input typed by the end user.</summary>
    User,

    /// <summary>Output produced by the model.</summary>
    Assistant,

    /// <summary>The result of a tool the assistant asked us to run.</summary>
    Tool
}

/// <summary>
/// A request from the model to invoke one of the registered tools.
/// </summary>
/// <param name="Id">Provider-assigned correlation id, echoed back with the result.</param>
/// <param name="Name">Registered tool name.</param>
/// <param name="Arguments">Arguments as a JSON object.</param>
public record ChatToolCall(string Id, string Name, JsonObject Arguments);

/// <summary>
/// The outcome of running a tool, sent back to the model.
/// </summary>
/// <param name="CallId">The <see cref="ChatToolCall.Id"/> this answers.</param>
/// <param name="Content">Serialized result payload.</param>
/// <param name="IsError">Whether the tool failed.</param>
public record ChatToolResult(string CallId, string Content, bool IsError = false);

/// <summary>
/// One turn in a conversation.
/// </summary>
public class ChatMessage
{
    /// <summary>Gets or sets the role of this message.</summary>
    public ChatRole Role { get; set; }

    /// <summary>Gets or sets the text content, if any.</summary>
    public string? Text { get; set; }

    /// <summary>Gets the tool calls the assistant requested in this turn.</summary>
    public IList<ChatToolCall> ToolCalls { get; } = new List<ChatToolCall>();

    /// <summary>Gets the tool results carried by this turn.</summary>
    public IList<ChatToolResult> ToolResults { get; } = new List<ChatToolResult>();
}

/// <summary>
/// A tool exposed to the model, described in a provider-neutral way.
/// </summary>
/// <param name="Name">Unique tool name.</param>
/// <param name="Description">What the tool does, written for the model.</param>
/// <param name="ParametersSchema">JSON Schema object describing the arguments.</param>
public record ChatToolDefinition(string Name, string Description, JsonObject ParametersSchema);

/// <summary>
/// A provider-neutral completion request.
/// </summary>
public class ChatRequest
{
    /// <summary>Gets or sets the system prompt. Always supplied by the plugin, never by the user.</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>Gets the conversation so far.</summary>
    public IList<ChatMessage> Messages { get; } = new List<ChatMessage>();

    /// <summary>Gets the tools the model is allowed to call this turn.</summary>
    public IList<ChatToolDefinition> Tools { get; } = new List<ChatToolDefinition>();

    /// <summary>Gets or sets the model identifier, as understood by the provider.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum tokens to generate.</summary>
    public int MaxTokens { get; set; } = 4096;
}

/// <summary>
/// Why the model stopped generating, normalized across providers.
/// </summary>
public enum ChatStopReason
{
    /// <summary>The model finished its answer.</summary>
    EndTurn,

    /// <summary>The model wants one or more tools executed before continuing.</summary>
    ToolUse,

    /// <summary>Generation hit the token ceiling.</summary>
    MaxTokens,

    /// <summary>The provider declined to answer.</summary>
    Refusal
}

/// <summary>
/// A provider-neutral completion response.
/// </summary>
/// <param name="Message">The assistant turn produced by the model.</param>
/// <param name="StopReason">Why generation stopped.</param>
public record ChatResponse(ChatMessage Message, ChatStopReason StopReason);

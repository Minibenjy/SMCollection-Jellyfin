using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AiAssistant.Api.Dtos;

/// <summary>A message sent by the user to the assistant.</summary>
public class ChatRequestDto
{
    /// <summary>Gets or sets the client-generated conversation identifier.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>Gets or sets the user's message.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>The assistant's reply.</summary>
/// <param name="Reply">Text to display.</param>
/// <param name="Success">Whether the assistant answered.</param>
/// <param name="NeedsConfirmation">Whether a state-changing action awaits approval.</param>
public record ChatReplyDto(string Reply, bool Success, bool NeedsConfirmation = false);

/// <summary>A user's answer to a confirmation prompt.</summary>
public class ConfirmRequestDto
{
    /// <summary>Gets or sets the conversation the prompt belongs to.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the action was approved.</summary>
    public bool Approved { get; set; }
}

/// <summary>What the assistant UI needs to render itself for the current user.</summary>
public class AssistantStatusDto
{
    /// <summary>Gets or sets a value indicating whether the assistant is usable right now.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a message explaining why it is unavailable, when it is.</summary>
    public string? Reason { get; set; }

    /// <summary>Gets or sets a value indicating whether the user may pick their own provider.</summary>
    public bool CanConfigure { get; set; }

    /// <summary>Gets or sets the name shown in the panel header.</summary>
    public string ServerLabel { get; set; } = string.Empty;
}

/// <summary>A provider offered to the user.</summary>
/// <param name="Id">Provider identifier.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="RequiresCredential">Whether an API key is needed.</param>
public record ProviderInfoDto(string Id, string DisplayName, bool RequiresCredential);

/// <summary>A user's own assistant settings, without secrets.</summary>
public class UserSettingsDto
{
    /// <summary>Gets or sets the chosen provider.</summary>
    public string ProviderId { get; set; } = string.Empty;

    /// <summary>Gets or sets the endpoint override.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the chosen model.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Gets or sets this user's override for the library's metadata language.</summary>
    public string MetadataLanguage { get; set; } = string.Empty;

    /// <summary>Gets or sets the server-wide metadata language, shown as the fallback. Read-only.</summary>
    public string ServerMetadataLanguage { get; set; } = string.Empty;

    /// <summary>Gets or sets the new API key to store. Never populated on read.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Gets or sets a masked hint of the stored key. Read-only.</summary>
    public string? ApiKeyHint { get; set; }

    /// <summary>Gets or sets the providers this user may choose from.</summary>
    public IReadOnlyList<ProviderInfoDto> AvailableProviders { get; set; } = Array.Empty<ProviderInfoDto>();
}

/// <summary>A conversation the user can return to.</summary>
/// <param name="Id">Conversation identifier.</param>
/// <param name="Title">First thing the user said in it.</param>
/// <param name="UpdatedUtc">When it was last active.</param>
public record ConversationSummaryDto(string Id, string Title, System.DateTimeOffset UpdatedUtc);

/// <summary>One visible turn of a conversation.</summary>
/// <param name="Role">"user" or "assistant".</param>
/// <param name="Text">What was said.</param>
public record TranscriptTurnDto(string Role, string Text);

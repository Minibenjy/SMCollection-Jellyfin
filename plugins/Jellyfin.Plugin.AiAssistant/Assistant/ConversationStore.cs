using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AiAssistant.Providers;

namespace Jellyfin.Plugin.AiAssistant.Assistant;

/// <summary>
/// Holds in-flight conversations server-side, keyed by user and conversation id.
/// </summary>
/// <remarks>
/// History is deliberately not round-tripped through the browser. If the client
/// posted the transcript back, a user could fabricate tool results and assistant
/// turns, which would let them talk their own assistant into believing things that
/// never came from the library. Keeping the transcript here means the only thing a
/// client can contribute is the next user message.
///
/// Conversations are memory-only and expire; nothing a user says to the assistant
/// is written to disk by this plugin.
/// </remarks>
public sealed class ConversationStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private const int MaxTurnsRetained = 40;

    private readonly ConcurrentDictionary<string, Entry> _conversations = new();
    private readonly ConcurrentDictionary<string, PendingAction?> _pending = new();

    /// <summary>
    /// Reads the transcript for a conversation.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <param name="conversationId">Conversation identifier.</param>
    /// <returns>The transcript, empty when unknown or expired.</returns>
    public IReadOnlyList<ChatMessage> Get(Guid userId, string conversationId)
    {
        Prune();
        return _conversations.TryGetValue(Key(userId, conversationId), out var entry)
            ? entry.Messages
            : Array.Empty<ChatMessage>();
    }

    /// <summary>
    /// Replaces the transcript for a conversation.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <param name="conversationId">Conversation identifier.</param>
    /// <param name="messages">The transcript to retain.</param>
    public void Set(Guid userId, string conversationId, IReadOnlyList<ChatMessage> messages)
    {
        var trimmed = messages.Count > MaxTurnsRetained
            ? messages.Skip(messages.Count - MaxTurnsRetained).ToList()
            : messages.ToList();

        var key = Key(userId, conversationId);

        // The title is taken from the first thing the person said and then left alone,
        // so a conversation keeps a stable name in the history list as it grows.
        var title = _conversations.TryGetValue(key, out var existing) && !string.IsNullOrEmpty(existing.Title)
            ? existing.Title
            : Summarize(messages);

        _conversations[key] = new Entry(trimmed, DateTimeOffset.UtcNow, title);
        Prune();
    }

    /// <summary>
    /// Lists a user's recent conversations, newest first.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <returns>Conversation id, title and last activity.</returns>
    public IReadOnlyList<(string Id, string Title, DateTimeOffset Updated)> List(Guid userId)
    {
        Prune();

        var prefix = userId.ToString("N") + ":";
        return _conversations
            .Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(p => p.Value.Touched)
            .Select(p => (p.Key[prefix.Length..], p.Value.Title, p.Value.Touched))
            .ToList();
    }

    /// <summary>
    /// Returns the visible turns of a conversation, for redisplay.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <param name="conversationId">Conversation identifier.</param>
    /// <returns>Role and text of each turn a person would have seen.</returns>
    public IReadOnlyList<(string Role, string Text)> GetTranscript(Guid userId, string conversationId)
        => Get(userId, conversationId)
            .Where(m => (m.Role == ChatRole.User || m.Role == ChatRole.Assistant)
                        && !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => (m.Role == ChatRole.User ? "user" : "assistant", m.Text!))
            .ToList();

    private static string Summarize(IReadOnlyList<ChatMessage> messages)
    {
        var first = messages.FirstOrDefault(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text));
        var text = first?.Text?.Trim() ?? "Conversation";
        return text.Length <= 60 ? text : text[..60] + "…";
    }

    /// <summary>
    /// Records a state-changing call awaiting the user's approval.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <param name="conversationId">Conversation identifier.</param>
    /// <param name="action">The pending action, or null to clear it.</param>
    public void SetPending(Guid userId, string conversationId, PendingAction? action)
        => _pending[Key(userId, conversationId)] = action;

    /// <summary>
    /// Takes the pending action for a conversation, clearing it.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <param name="conversationId">Conversation identifier.</param>
    /// <returns>The action, or null when nothing is awaiting approval.</returns>
    public PendingAction? TakePending(Guid userId, string conversationId)
    {
        // Taken rather than read: an approval must not be replayable into a second
        // execution of the same write.
        _pending.TryRemove(Key(userId, conversationId), out var action);
        return action;
    }

    /// <summary>
    /// Forgets a conversation.
    /// </summary>
    /// <param name="userId">Owning user.</param>
    /// <param name="conversationId">Conversation identifier.</param>
    public void Clear(Guid userId, string conversationId)
    {
        _conversations.TryRemove(Key(userId, conversationId), out _);
        _pending.TryRemove(Key(userId, conversationId), out _);
    }

    private static string Key(Guid userId, string conversationId)
        => userId.ToString("N") + ":" + conversationId;

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - Lifetime;
        foreach (var pair in _conversations)
        {
            if (pair.Value.Touched < cutoff)
            {
                _conversations.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record Entry(IReadOnlyList<ChatMessage> Messages, DateTimeOffset Touched, string Title);
}

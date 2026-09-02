using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.AiAssistant.Providers;

/// <summary>
/// Recovers tool calls that a model wrote into its reply text instead of emitting.
/// </summary>
/// <remarks>
/// Small local models do this constantly. Told to add items to a playlist, a 7B model
/// answered with a fenced JSON block reading
/// <c>{"name": "add_to_playlist", "arguments": {…}}</c> — the right call, with the
/// right arguments, in the wrong field. Nothing ran, and the user was shown a wall of
/// JSON as if it were an answer.
///
/// Structured tool calling is still what is asked for and what is used when it
/// arrives. This is the fallback for when it does not: a candidate is accepted only
/// if it names a tool that was actually offered this turn, which keeps ordinary text
/// that happens to contain JSON from being executed.
/// </remarks>
internal static class ToolCallSalvage
{
    /// <summary>
    /// Looks for a tool call embedded in reply text.
    /// </summary>
    /// <param name="text">The model's reply text.</param>
    /// <param name="offered">Names of the tools offered this turn.</param>
    /// <param name="call">The recovered call.</param>
    /// <param name="remainingText">The reply with the recovered block removed.</param>
    /// <returns>True when a call was recovered.</returns>
    public static bool TryRecover(
        string? text,
        IReadOnlyCollection<string> offered,
        out ChatToolCall? call,
        out string? remainingText)
    {
        call = null;
        remainingText = text;

        if (string.IsNullOrWhiteSpace(text) || offered.Count == 0)
        {
            return false;
        }

        foreach (var (json, start, length) in JsonObjectsIn(text))
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                continue;
            }

            if (node is not JsonObject candidate)
            {
                continue;
            }

            var name = ReadString(candidate, "name") ?? ReadString(candidate, "tool")
                       ?? ReadString(candidate, "function") ?? ReadString(candidate, "tool_name");

            if (name is null
                || !offered.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            var arguments = candidate["arguments"] as JsonObject
                            ?? candidate["parameters"] as JsonObject
                            ?? candidate["input"] as JsonObject
                            ?? new JsonObject();

            call = new ChatToolCall("call_salvaged", name, (JsonObject)arguments.DeepClone());

            // Whatever prose surrounded the block is kept, so a model that explained
            // itself and then wrote the call does not lose the explanation. The block
            // itself goes, because it is machinery, not an answer.
            remainingText = (text[..start] + text[(start + length)..]).Trim();
            return true;
        }

        return false;
    }

    private static string? ReadString(JsonObject node, string key)
        => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// Yields every balanced <c>{…}</c> span in the text, outermost first.
    /// </summary>
    /// <remarks>
    /// Brace counting rather than a regular expression, because the interesting
    /// candidates nest: the arguments object sits inside the call object, and a
    /// non-greedy pattern finds only the inner one.
    /// </remarks>
    private static IEnumerable<(string Json, int Start, int Length)> JsonObjectsIn(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '{')
            {
                continue;
            }

            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var j = i; j < text.Length; j++)
            {
                var c = text[j];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    continue;
                }

                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        yield return (text[i..(j + 1)], i, j + 1 - i);
                        i = j;
                        break;
                    }
                }
            }
        }
    }
}

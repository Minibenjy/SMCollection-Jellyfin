using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// Reads tool arguments tolerantly.
/// </summary>
/// <remarks>
/// A tool's JSON Schema is a hint to the model, not a contract it is capable of
/// honouring. Small local models routinely send a number as "5", a single value
/// where an array is declared, or a boolean as "true", and a strict read throws.
/// Because the model cannot see the exception, it simply retries the same malformed
/// call until the tool-call budget runs out — one sloppy type costs the whole
/// exchange.
///
/// Coercing here is not laxity about validation: the values still end up clamped and
/// range-checked by the caller. It just moves the failure from "crash" to "read what
/// was obviously meant", which is what keeps small models usable.
/// </remarks>
public static class ToolArguments
{
    /// <summary>Reads a string argument.</summary>
    /// <param name="arguments">The model's arguments.</param>
    /// <param name="name">Argument name.</param>
    /// <returns>The value, or null when absent.</returns>
    public static string? GetString(JsonObject arguments, string name)
    {
        var node = arguments[name];
        if (node is not JsonValue value)
        {
            return node?.ToString();
        }

        if (value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return value.ToString();
    }

    /// <summary>Reads an integer argument, accepting numeric strings and floats.</summary>
    /// <param name="arguments">The model's arguments.</param>
    /// <param name="name">Argument name.</param>
    /// <param name="fallback">Value to use when absent or unreadable.</param>
    /// <returns>The value.</returns>
    public static int GetInt(JsonObject arguments, string name, int fallback)
    {
        if (arguments[name] is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<int>(out var direct))
        {
            return direct;
        }

        if (value.TryGetValue<double>(out var asDouble))
        {
            return (int)Math.Round(asDouble);
        }

        if (value.TryGetValue<string>(out var text)
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        // Some models wrap numbers oddly; fall back to the raw token before giving up.
        return int.TryParse(
            value.ToString(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var loose)
            ? loose
            : fallback;
    }

    /// <summary>Reads a boolean, accepting the strings and numbers models send instead.</summary>
    /// <param name="arguments">The model's arguments.</param>
    /// <param name="name">Argument name.</param>
    /// <param name="fallback">Value to use when absent or unreadable.</param>
    /// <returns>The value.</returns>
    public static bool GetBool(JsonObject arguments, string name, bool fallback)
    {
        if (arguments[name] is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<bool>(out var direct))
        {
            return direct;
        }

        var raw = (value.TryGetValue<string>(out var text) ? text : value.ToString()).Trim();

        return raw.ToLowerInvariant() switch
        {
            "true" or "yes" or "1" or "y" => true,
            "false" or "no" or "0" or "n" => false,
            _ => fallback
        };
    }

    /// <summary>
    /// Reads a list of strings, accepting a bare value or a comma-separated string
    /// where an array was declared.
    /// </summary>
    /// <param name="arguments">The model's arguments.</param>
    /// <param name="name">Argument name.</param>
    /// <returns>The values, empty when absent.</returns>
    public static IReadOnlyList<string> GetStringList(JsonObject arguments, string name)
    {
        var node = arguments[name];
        var values = new List<string>();

        switch (node)
        {
            case null:
                return values;

            case JsonArray array:
                foreach (var entry in array)
                {
                    var text = entry is JsonValue v && v.TryGetValue<string>(out var s) ? s : entry?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        values.Add(text.Trim());
                    }
                }

                return values;

            case JsonValue value:
                var raw = value.TryGetValue<string>(out var single) ? single : value.ToString();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return values;
                }

                // A JSON array that arrived as a string, or a plain comma-separated list.
                if (raw.TrimStart().StartsWith('['))
                {
                    try
                    {
                        if (JsonNode.Parse(raw) is JsonArray parsed)
                        {
                            var wrapper = new JsonObject { [name] = parsed };
                            return GetStringList(wrapper, name);
                        }
                    }
                    catch (JsonException)
                    {
                        // Models also emit Python-style lists — ['Movie'] — which are not
                        // valid JSON. Strip the brackets and quotes and split by comma.
                        raw = raw.Trim().Trim('[', ']');
                    }
                }

                foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var cleaned = part.Trim('\'', '"', ' ');
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        values.Add(cleaned);
                    }
                }

                return values;

            default:
                return values;
        }
    }
}

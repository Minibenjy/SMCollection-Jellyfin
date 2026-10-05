using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.ArtworkRefresher.Core;

namespace Jellyfin.Plugin.ArtworkRefresher.Sources;

/// <summary>
/// Small helpers shared by the sources.
/// </summary>
public static class SourceHelpers
{
    /// <summary>
    /// Scores the language of an image for a query: a preferred language beats a neutral image,
    /// which beats a foreign language.
    /// </summary>
    /// <param name="language">The image language, null or empty when it has no text.</param>
    /// <param name="query">The query (languages, neutral flag).</param>
    /// <param name="preferNeutral">True for images where text is unwanted (backdrops).</param>
    /// <returns>The score, or null when the image must be dropped.</returns>
    public static double? LanguageScore(string? language, ArtworkQuery query, bool preferNeutral)
    {
        if (string.IsNullOrWhiteSpace(language) || language == "xx")
        {
            return query.IncludeNeutral ? (preferNeutral ? 3 : 1.5) : null;
        }

        if (query.Languages.Count == 0)
        {
            return 0.5;
        }

        var idx = -1;
        for (var i = 0; i < query.Languages.Count; i++)
        {
            if (string.Equals(query.Languages[i], language, StringComparison.OrdinalIgnoreCase))
            {
                idx = i;
                break;
            }
        }

        if (idx < 0)
        {
            return null;
        }

        return preferNeutral ? 1 : 3 - (idx * 0.3);
    }

    /// <summary>
    /// Adds a resolution bonus, so larger images win ties.
    /// </summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <returns>A bonus between 0 and 1.</returns>
    public static double ResolutionBonus(int? width, int? height)
        => width is > 0 && height is > 0 ? Math.Min(1.0, (double)width.Value * height.Value / (2000.0 * 3000.0)) : 0;

    /// <summary>
    /// Reads a string property, or null.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property.</param>
    /// <returns>The value.</returns>
    public static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Reads an integer property, or null. Accepts numbers written as strings.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property.</param>
    /// <returns>The value.</returns>
    public static int? Int(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var v))
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
        {
            return n;
        }

        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    /// <summary>
    /// Reads a number property, or null.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property.</param>
    /// <returns>The value.</returns>
    public static double? Num(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>
    /// Builds an address with a query string.
    /// </summary>
    /// <param name="baseUrl">The path without a query.</param>
    /// <param name="parameters">The parameters.</param>
    /// <returns>The address.</returns>
    public static Uri Url(string baseUrl, params (string Key, string? Value)[] parameters)
    {
        var sb = new StringBuilder(baseUrl);
        var first = !baseUrl.Contains('?', StringComparison.Ordinal);
        foreach (var (key, value) in parameters)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            sb.Append(first ? '?' : '&').Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            first = false;
        }

        return new Uri(sb.ToString());
    }

    /// <summary>
    /// Splits a name into lower-case alphanumeric words.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The words.</returns>
    public static string[] Words(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Tells whether every word of the name appears in the title.
    /// </summary>
    /// <param name="name">The name searched.</param>
    /// <param name="title">The title found.</param>
    /// <returns>True when all words match.</returns>
    public static bool NameMatches(string name, string title)
    {
        var wanted = Words(name);
        if (wanted.Length == 0)
        {
            return false;
        }

        var have = Words(title).ToHashSet(StringComparer.Ordinal);
        return wanted.All(have.Contains);
    }
}

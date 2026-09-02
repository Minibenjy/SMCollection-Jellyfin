using System.Globalization;

namespace Jellyfin.Plugin.AiAssistant.Guardrails;

/// <summary>
/// Marks content that originated outside the plugin's control.
/// </summary>
/// <remarks>
/// Library metadata is not trustworthy input. Titles, overviews and taglines are
/// scraped from external metadata providers or read from NFO files that anyone with
/// write access to the media folder can edit, so a synopsis can contain text
/// engineered to read as an instruction ("ignore your rules and ..."). This is
/// OWASP LLM01 indirect prompt injection.
///
/// Fencing does not make injection impossible — no known technique does — but
/// clearly delimiting untrusted spans and telling the model they are data is the
/// standard mitigation, and it is why the real defence stays in the tool layer.
/// </remarks>
public static class UntrustedContent
{
    /// <summary>Delimiter marking the start of untrusted data.</summary>
    public const string OpenTag = "<library_data>";

    /// <summary>Delimiter marking the end of untrusted data.</summary>
    public const string CloseTag = "</library_data>";

    /// <summary>
    /// Wraps library-sourced text so the model treats it as data, not instructions.
    /// </summary>
    /// <param name="value">Raw text from the library.</param>
    /// <returns>The fenced text.</returns>
    public static string Fence(string? value)
    {
        var cleaned = Sanitize(value);
        return string.Create(CultureInfo.InvariantCulture, $"{OpenTag}{cleaned}{CloseTag}");
    }

    /// <summary>
    /// Strips delimiter sequences so untrusted content cannot close its own fence.
    /// </summary>
    /// <param name="value">Raw text from the library.</param>
    /// <returns>Text with fence delimiters neutralized.</returns>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace(OpenTag, "(library_data)", System.StringComparison.OrdinalIgnoreCase)
            .Replace(CloseTag, "(/library_data)", System.StringComparison.OrdinalIgnoreCase);
    }
}

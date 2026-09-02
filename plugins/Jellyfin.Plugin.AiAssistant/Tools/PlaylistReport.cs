using System.Linq;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AiAssistant.Guardrails;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// Turns a playlist write into a result the model can report honestly.
/// </summary>
/// <remarks>
/// The old result said only <c>item_count</c>. That is enough to claim success and
/// not enough to notice failure: a playlist built from three shows where one silently
/// contributed nothing came back as <c>created: true, item_count: 3</c> and was
/// announced to the user as all three. Naming what actually went in — and what did
/// not, and why — is what lets the model tell the truth without being asked to.
/// </remarks>
internal static class PlaylistReport
{
    /// <summary>
    /// Adds the contents and the casualties to a write result.
    /// </summary>
    /// <param name="payload">The result so far.</param>
    /// <param name="resolution">What resolving the model's references produced.</param>
    /// <returns>The completed result.</returns>
    public static JsonObject Build(JsonObject payload, PlaylistResolution resolution)
    {
        payload["item_count"] = resolution.Items.Count;
        payload["items"] = new JsonArray(resolution.Items
            .Select(i => (JsonNode)UntrustedContent.Sanitize(Label(i)))
            .ToArray());

        if (resolution.Expanded.Count > 0)
        {
            payload["expanded"] = new JsonArray(resolution.Expanded.Select(e => (JsonNode)e).ToArray());
        }

        if (resolution.Rejected.Count > 0)
        {
            payload["rejected"] = new JsonArray(resolution.Rejected.Select(r => (JsonNode)r).ToArray());
            payload["you_must_tell_the_user"] =
                "Some of what you asked for did not go in. Say which, and why, rather than "
                + "reporting the playlist as complete.";
        }

        return payload;
    }

    private static string Label(MediaBrowser.Controller.Entities.BaseItem item)
        => item is MediaBrowser.Controller.Entities.TV.Episode episode
            ? $"{episode.SeriesName} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00} — {episode.Name}"
            : item.Name;
}

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AiAssistant.Guardrails;
using MediaBrowser.Controller.Playlists;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// Finds the playlists a user owns.
/// </summary>
/// <remarks>
/// Every playlist tool starts here rather than from a raw id, so a playlist the
/// acting user does not own can never be read or edited — the id the model supplies
/// is matched against their own list instead of being trusted and looked up.
///
/// A name is accepted as well as an id because that is what people say and what
/// models therefore pass: "add it to my anime playlist" carries no id anywhere.
/// </remarks>
internal sealed class PlaylistAccess
{
    private readonly IPlaylistManager _playlistManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistAccess"/> class.
    /// </summary>
    /// <param name="playlistManager">Playlist manager.</param>
    public PlaylistAccess(IPlaylistManager playlistManager)
    {
        _playlistManager = playlistManager;
    }

    /// <summary>
    /// Lists the user's playlists, newest first.
    /// </summary>
    /// <param name="scope">The acting user.</param>
    /// <returns>Their playlists.</returns>
    public IReadOnlyList<Playlist> All(UserScope scope)
        => _playlistManager.GetPlaylists(scope.UserId)
            .OrderByDescending(p => p.DateCreated)
            .ToList();

    /// <summary>
    /// Finds one playlist by id or by name.
    /// </summary>
    /// <param name="scope">The acting user.</param>
    /// <param name="reference">An id in "N" form, or the playlist's name.</param>
    /// <returns>The playlist, or null when the user has no such playlist.</returns>
    public Playlist? Find(UserScope scope, string reference)
    {
        var raw = reference.Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        var playlists = All(scope);

        if (Guid.TryParse(raw, out var id))
        {
            return playlists.FirstOrDefault(p => p.Id == id);
        }

        return playlists.FirstOrDefault(p => string.Equals(p.Name, raw, StringComparison.OrdinalIgnoreCase))
               ?? playlists.FirstOrDefault(p => p.Name.Contains(raw, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds the "no such playlist" result, naming what the user does have.
    /// </summary>
    /// <remarks>
    /// A bare failure sends the model looking for the playlist with search_library,
    /// which does not find playlists reliably, after which it concludes the playlist
    /// is absent and creates a duplicate. Listing the real names ends that loop in
    /// one turn.
    /// </remarks>
    /// <param name="scope">The acting user.</param>
    /// <returns>An error message listing the available playlists.</returns>
    public string NotFoundMessage(UserScope scope)
    {
        var names = All(scope).Select(p => UntrustedContent.Sanitize(p.Name)).Take(20).ToList();

        return names.Count == 0
            ? "This user has no playlists at all yet."
            : "No playlist of this user matches that id or name. Their playlists are: "
              + string.Join("; ", names) + ".";
    }
}

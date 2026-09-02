using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AiAssistant.Guardrails;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AiAssistant.Tools;

/// <summary>
/// Turns whatever the model passed as an item reference into a real item the acting
/// user can see.
/// </summary>
/// <remarks>
/// Models pass names where ids are declared, ids of the wrong thing, and ids they
/// have carried over from an earlier tool result. Rejecting all of that is correct
/// but expensive: every rejection costs a round trip, and a small model often gives
/// up rather than correcting itself. So the tolerant part happens here, and the
/// authorization part happens here too — every resolution runs against the user, so
/// nothing this returns is something they could not already see.
/// </remarks>
internal sealed class ItemResolver
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemResolver"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    public ItemResolver(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Resolves an id or a name to a single item.
    /// </summary>
    /// <param name="scope">The acting user.</param>
    /// <param name="reference">An id in "N" form, or a title.</param>
    /// <param name="kinds">Kinds to restrict a name lookup to, empty for any.</param>
    /// <returns>The item, or null.</returns>
    public BaseItem? Resolve(UserScope scope, string reference, params BaseItemKind[] kinds)
    {
        var raw = reference.Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        if (Guid.TryParse(raw, out var id))
        {
            var byId = _libraryManager.GetItemById(id);
            return byId is not null && byId.IsVisible(scope.User) ? byId : null;
        }

        var found = _libraryManager.GetItemsResult(new InternalItemsQuery(scope.User)
        {
            IncludeItemTypes = kinds,
            SearchTerm = raw,
            Recursive = true,
            Limit = 1
        });

        return found.Items.Count > 0 ? found.Items[0] : ResolveInsideParent(scope, raw, kinds);
    }

    /// <summary>
    /// Resolves references of the form "Parent - Child", by looking inside the parent.
    /// </summary>
    /// <remarks>
    /// This is what people and models both write for an episode: "Medabots - Episodio
    /// 13", "Kim Possible: Gone Fishin'". As one search term it matches nothing, because
    /// no single item is called that. Told to look up the id first, a small model
    /// carries on passing the compound name anyway — so the compound name is understood
    /// here instead.
    ///
    /// The search is deliberately narrow: it only ever returns something inside the
    /// parent, never the parent itself. Widening "Medabots - Episodio 13" to "Medabots"
    /// would resolve to the whole series, and a caller like set_watched would then mark
    /// thirty-nine episodes watched on the strength of a request about one.
    /// </remarks>
    private BaseItem? ResolveInsideParent(UserScope scope, string raw, BaseItemKind[] kinds)
    {
        var split = raw.Split(Separators, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (split.Length != 2)
        {
            return null;
        }

        var parent = _libraryManager.GetItemsResult(new InternalItemsQuery(scope.User)
        {
            SearchTerm = split[0],
            Recursive = true,
            Limit = 1
        });

        if (parent.Items.Count == 0 || parent.Items[0] is not Folder folder)
        {
            return null;
        }

        var child = split[1];

        var byName = _libraryManager.GetItemsResult(new InternalItemsQuery(scope.User)
        {
            IncludeItemTypes = kinds,
            ParentId = folder.Id,
            SearchTerm = child,
            Recursive = true,
            Limit = 1
        });

        if (byName.Items.Count > 0)
        {
            return byName.Items[0];
        }

        // "Episodio 13", "capítulo 13", "ep 13", or a bare "13": the number is the only
        // part that identifies anything when the episode has no real title, which is
        // exactly the library where the name lookup above was always going to fail.
        var number = NumberIn(child);
        if (number is null)
        {
            return null;
        }

        var numbered = _libraryManager.GetItemsResult(new InternalItemsQuery(scope.User)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            ParentId = folder.Id,
            Recursive = true,
            IndexNumber = number
        });

        return numbered.Items.Count == 1 ? numbered.Items[0] : null;
    }

    private static readonly string[] Separators = { " - ", " – ", " — ", ": ", " / " };

    private static int? NumberIn(string text)
    {
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return digits.Length is > 0 and <= 4 && int.TryParse(digits, out var value) ? value : null;
    }

    /// <summary>
    /// Resolves a reference to the series it belongs to.
    /// </summary>
    /// <remarks>
    /// Models routinely pass the id of an episode or season they just found rather
    /// than the series'. Walking up is unambiguous and saves a wasted round trip.
    /// </remarks>
    /// <param name="scope">The acting user.</param>
    /// <param name="reference">An id or a series name.</param>
    /// <returns>The series, or null.</returns>
    public Series? ResolveSeries(UserScope scope, string reference)
    {
        var requested = Resolve(scope, reference, BaseItemKind.Series);

        var series = requested switch
        {
            Series direct => direct,
            Episode fromEpisode => _libraryManager.GetItemById(fromEpisode.SeriesId) as Series,
            Season fromSeason => _libraryManager.GetItemById(fromSeason.SeriesId) as Series,
            _ => null
        };

        return series is not null && series.IsVisible(scope.User) ? series : null;
    }

    /// <summary>
    /// Resolves the ids the model wants to put in a playlist.
    /// </summary>
    /// <remarks>
    /// This is the fix for the failure that produced three identical playlists: the
    /// model handed back a <c>playlist_id</c> from an earlier tool result as though it
    /// were an item id. Jellyfin quietly expanded it and reported success, so nothing
    /// downstream could tell that the playlist was a clone of the previous one.
    ///
    /// Containers are now expanded deliberately and reported, so "add season 1" works
    /// and is visible in the result; a playlist reference is refused outright, because
    /// the only reason to have one here is the mistake above.
    /// </remarks>
    /// <param name="scope">The acting user.</param>
    /// <param name="references">Ids or names supplied by the model.</param>
    /// <param name="max">Cap on resolved items.</param>
    /// <returns>The resolution.</returns>
    public PlaylistResolution ResolveForPlaylist(
        UserScope scope,
        IReadOnlyList<string> references,
        int max)
    {
        var items = new List<BaseItem>();
        var seen = new HashSet<Guid>();
        var rejected = new List<string>();
        var expanded = new List<string>();

        foreach (var reference in references)
        {
            var item = Resolve(scope, reference);

            if (item is null)
            {
                rejected.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"\"{UntrustedContent.Sanitize(reference)}\": nothing in this library has that id or name."));
                continue;
            }

            if (item.GetBaseItemKind() == BaseItemKind.Playlist)
            {
                rejected.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"\"{UntrustedContent.Sanitize(item.Name)}\" is a playlist, not something that can go in one. If you meant to add its contents, call get_playlist to read them first. Never pass a playlist_id from an earlier result as an item id."));
                continue;
            }

            // Books, comics and magazines are a dead end here — not a gap in this
            // plugin, but Jellyfin's playlist system itself: verified directly against
            // the server's own API, bypassing this plugin entirely, that a Book id
            // handed to POST /Playlists is accepted and then silently dropped, same as
            // here. The generic "holds nothing playable" message that used to come out
            // of this case left the model nowhere to go but repeat itself or give up.
            // Naming the real alternatives — favouriting works for any item kind, and
            // an administrator can put one in a Collection, which does accept books —
            // turns a dead end into a redirect.
            if (item.GetBaseItemKind() == BaseItemKind.Book)
            {
                rejected.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"\"{UntrustedContent.Sanitize(item.Name)}\" is a book, comic or magazine. Jellyfin playlists cannot contain these at all — this is not something to retry or work around. Use set_favorite to mark it for this user instead, or create_collection if they are a server administrator."));
                continue;
            }

            var resolved = ExpandContainer(scope, item, out var wasExpanded);

            if (resolved.Count == 0)
            {
                rejected.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"\"{UntrustedContent.Sanitize(item.Name)}\" ({item.GetBaseItemKind()}) holds nothing playable."));
                continue;
            }

            if (wasExpanded)
            {
                expanded.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"\"{UntrustedContent.Sanitize(item.Name)}\" expanded to {resolved.Count} item(s)"));
            }

            foreach (var child in resolved)
            {
                if (items.Count >= max)
                {
                    break;
                }

                if (seen.Add(child.Id))
                {
                    items.Add(child);
                }
            }
        }

        return new PlaylistResolution(items, rejected, expanded);
    }

    private IReadOnlyList<BaseItem> ExpandContainer(UserScope scope, BaseItem item, out bool expanded)
    {
        var kind = item.GetBaseItemKind();

        if (ItemProjection.PlayableKinds.Contains(kind))
        {
            expanded = false;
            return new[] { item };
        }

        expanded = true;

        // A folder-shaped item — a series, a season, an album, a box set — is what a
        // person means when they say "add Firefly to the playlist". Enumerate it as
        // the user, in the order they would see it.
        var children = _libraryManager.GetItemsResult(new InternalItemsQuery(scope.User)
        {
            IncludeItemTypes = ItemProjection.PlayableKinds.ToArray(),
            Recursive = true,
            ParentId = item.Id,
            Limit = 500,
            OrderBy = new[]
            {
                (ItemSortBy.ParentIndexNumber, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending),
                (ItemSortBy.IndexNumber, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending),
                (ItemSortBy.SortName, Jellyfin.Database.Implementations.Enums.SortOrder.Ascending)
            }
        });

        return children.Items.ToList();
    }
}

/// <summary>
/// What came of resolving the model's item references.
/// </summary>
/// <param name="Items">Items that resolved, de-duplicated and in order.</param>
/// <param name="Rejected">Human-readable reasons for each reference that did not.</param>
/// <param name="Expanded">Notes about containers that were unfolded.</param>
internal sealed record PlaylistResolution(
    IReadOnlyList<BaseItem> Items,
    IReadOnlyList<string> Rejected,
    IReadOnlyList<string> Expanded);

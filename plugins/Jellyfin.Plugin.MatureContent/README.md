# Mature Content

Hides or reveals adult content behind a single toggle, using Jellyfin's own tags
and its own permission system rather than inventing a parallel one.

## Marking content

Edit the metadata of any item, folder, series, collection or whole library and add
one of these tags:

- `mature`
- `+18`

Or use the plugin's own UI, which does the same thing without going through the
metadata editor:

- **Detail page** — an "M" button marks or unmarks the item you are looking at.
- **Multi-select** — two buttons mark or unmark everything selected.
- **Plugin page** — Dashboard → Plugins → Mature Content, with a library browser
  and a search box.

Tagging a **library's root folder** is the efficient move: the tag is inherited by
everything inside it.

## How hiding works

The tag is added to the user's `UserPolicy.BlockedTags`, which Jellyfin already
honours everywhere it lists content — views, search, recommendations. Nothing has
to be re-implemented, and there is no way for the content to leak through a listing
the plugin did not know about.

## The toggle

An "M" badge appears in the topbar for accounts allowed to use it: dimmed when
mature content is hidden, red with a glow when it is visible. Administrators get it
by default; the plugin page decides per account both whether the toggle appears and,
for accounts without it, whether mature content is visible or hidden.

## Durable marks

Marked item ids are kept in `MarkedItemIds` in the plugin configuration, not only
as tags. A metadata refresh can wipe an item's tags — so `MatureStartupHostedService`
(at startup) and `MatureLibraryPostScanTask` (after every library scan) re-apply the
tag to anything in the history that lost it.

`POST /MatureContent/Sync` reconciles the other direction: it discovers items that
already carry the tags and adds them to the history, and reports what it found.

## API

All endpoints require elevation.

| Endpoint | Purpose |
|---|---|
| `GET /MatureContent/State` | Whether the caller may toggle, and current visibility |
| `GET /MatureContent/Users` | Per-user toggle and visibility settings |
| `GET /MatureContent/MarkedItems` | The full history, resolved against the library |
| `POST /MatureContent/Items/{id}` | Mark or unmark one item |
| `POST /MatureContent/Sync` | Reconcile tags and history |
| `POST /MatureContent/Users/{userId}/Visible` | Change one account's visibility now |
| `POST /MatureContent/ApplyDefaults` | Apply the default policy to all accounts |

## Known limitations

- This hides content from the **Jellyfin UI**. Someone with the file paths and
  filesystem access can still reach the files; it is a household convenience, not
  a security boundary.
- Tag inheritance is Jellyfin's, so an item whose library is tagged is hidden even
  if you unmark the item itself. Unmark at the level you tagged.

## Implementation notes

- `ApiClient.ajax` calls need `dataType: 'json'` — without it the web client hands
  back a raw `Response` object instead of parsed JSON, and the plugin's own UI
  silently reads `undefined` for everything. Both the client script and the config
  page set it explicitly.
- `_userManager.GetUserDto` reads stale within the same request after
  `UpdatePolicyAsync`, so the endpoints return the value they just wrote rather than
  re-reading it.
- `GetItemList` with a bare recursive query throws `Cannot deserialize unknown type`
  on legacy or live-TV rows, so `Sync` filters by concrete `BaseItemKind` values and
  walks `RootFolder.Children` separately for `CollectionFolder`s.

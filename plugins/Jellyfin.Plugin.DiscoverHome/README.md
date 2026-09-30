# Discover Home

Turns the Jellyfin home screen into something closer to a discovery front page:
a sidebar pinned open on desktop, a centered search box with live suggestions,
denser poster cards, and genre and studio carousels shuffled in between
Jellyfin's own rows on every load.

Everything here is presentation. The plugin reorders and restyles rows the
server already rendered for that user — it never changes what anyone is allowed
to see, so none of its settings carry a permission decision.

## What it does

| | |
|---|---|
| **Pinned sidebar** | Jellyfin's drawer is an overlay with no "pinned rail" mode, so the plugin builds one and offsets the header, the page and the Media Bar hero to match. Below a configurable breakpoint it falls back to the stock hamburger drawer. |
| **Sidebar restyle** | Groups reordered libraries-first, 40px rounded rows, fixed-width icons, group headers in muted small caps. The logo moves in from the header. |
| **Centered search** | Absolutely centered in the header, with live suggestions from `/Search/Hints`. Replaces the Home/Favourites tabs, which duplicate the pinned sidebar. |
| **Compact cards** | Configurable poster width, tighter captions, and a media-type pill drawn entirely in CSS. |
| **Genre & studio carousels** | Inserted between native rows at a random gap, never the same type twice running. Genre tiles get a collage of posters from that genre; studio tiles get a real logo when one is cached. |
| **Daily artwork sync** | A scheduled task downloads studio logos for the studios this library actually contains. |

All of it is configurable from **Dashboard → Plugins → Discover Home**, including
switching the whole layer off without uninstalling.

## Artwork

Jellyfin ships no studio artwork of its own — `/Studios/{name}/Images/Primary`
returns 404 for every studio on a stock install — so logos have to come from
somewhere public. The default source is the same community repository Jellyfin's
own Studio Images plugin points at, which publishes a flat `thumbs.txt` index of
~3200 studio names plus one `thumb.jpg` per studio:

```
{base}/thumbs.txt                      index, one studio name per line
{base}/images/{studio}/thumb.jpg       the logo
```

Only studios present in this library are downloaded, so the cache stays
proportional to the collection rather than to the upstream index. A studio the
index doesn't cover simply keeps its coloured, titled tile, and the client
never requests a logo for it.

Genres are deliberately **not** downloaded. There is no public genre-artwork
source — Seerr, the design this borrows from, ships a hand-curated mapping of
TMDB backdrops — so genre tiles are instead composed in the browser from posters
already in that genre. Nothing to fetch, nothing to keep in sync, and the result
always reflects the actual library.

## Notes for the next person in here

These are the things that cost real time to work out. They are not obvious from
the markup, and every one of them was measured on a live server rather than
guessed.

**Never move Jellyfin's rows.** Home rows are `emby-scroller` custom elements.
Re-parenting one fires its attach/detach callbacks and breaks its horizontal
scrolling (`TypeError: e.addScrollEventListener is not a function`). All
ordering is done by writing `style.order` and making the container a flex
column, which leaves every node exactly where it was in the DOM.

**The row gutter is a percentage.** Native rows are inset by `padding-left:
3.3%` via Jellyfin's `padded-left` class — not a px or em value, so no constant
can match it at every width. The carousels reuse `padded-left` and
`sectionTitle-cards` directly instead of styling their own gutter.

**Don't put `scroll-snap` on the carousels.** With `scroll-snap-align: start`
on the tiles, the browser snaps the first tile to the scrollport edge on load,
which sets `scrollLeft` to exactly the gutter width and silently cancels the
gutter out. Native rows don't snap either.

**Empty sections are real.** Disabling a feature elsewhere (KefinTweaks'
discovery engine, for one) leaves behind `verticalSection` placeholders with no
children and no height. Counting those as rows makes the inserted carousels look
bunched together, because the gaps between them are filled with invisible
elements. `hasRealContent()` requires an actual `.card` before a section counts.

**The home screen fills in progressively.** A one-shot pass that locks itself
after the first run only ever sees the handful of rows that existed in its
settle window; everything that arrives later stays unstyled. Processing is
per-section (`dataset.dhDone`) and the ordering counters live at module scope so
later batches continue the same sequence instead of restarting.

**`.mainDrawer` is a flex row, not a column.** Putting the logo there as a
sibling steals horizontal space from the nav labels (they start ellipsising) and
`margin: auto` centres on the wrong axis. It belongs inside
`.mainDrawer-scrollContainer`, which is plain block flow. And once that
container is made a flex column for reordering, the logo needs `flex-shrink: 0`
or it collapses to zero height.

**The page ignores its parent's margin.** `.mainAnimatedPages` is
`position: static`, but its `.page` children are `position: absolute` with
`left: 0; right: 0`, sized against the viewport. Offsetting for the sidebar has
to set `left` on the page itself. The Media Bar hero (`.layout-marquee`) is a
direct child of `<body>` and needs its own offset again.

**A lazy image needs a layout box.** `loading="lazy"` only fetches an image
once it is near the viewport — and an image that is `display: none`, or not in
the document at all, is never near anything. Both "append on load" and "hide
until load" deadlock: the logo is never fetched, so it never loads. The client
asks `/DiscoverHome/Art/Studios` which studios have a logo, and only those
tiles get an image, visible from the start.

**Size the logo tile explicitly.** A tile's width is `auto`, so a percentage
`max-width` on its image resolves against a box the image itself is sizing, and
the tile grows to the thumbnail's natural ~1000px. The upstream artwork is a
16:9 thumbnail with its own background, not a transparent logo, so the tile is
16:9 at the same height as a titled tile and the image covers it.

**Home state is per container.** Every visit to Home builds a new
`.homeSectionsContainer`. Ordering counters and the genre/studio cursors are
kept per container (a `WeakMap`), not globally — a global cursor runs out of
entries after a few round trips and Home loses its carousels.

**This server's `/Items` caps at 16.** A generic `/Items?Recursive=true` query
returns at most 16 rows regardless of `Limit` — some other plugin's middleware,
never tracked down. Search suggestions use `/Search/Hints` instead, which is
Jellyfin's own indexed search endpoint and has no such ceiling.

**Per-user settings live in DisplayPreferences.** The "Customize home" panel
writes `dh.*` keys into `/DisplayPreferences/discoverhome?client=discoverhome`
for the signed-in user. The plugin keeps no user data of its own; the client
overlays those keys on the administrator's `/DiscoverHome/Settings`.

## Settings reference

Layout: enable/disable, pin sidebar, breakpoint, reorder sidebar, logo in
sidebar, collapsible sidebar groups, per-user customization, centered search,
hide random button.
Cards: compact cards, card width, large cards on desktop, type pills, accent colour, pill text per type.
Rows: shuffle, genre row, studio row, min/max gap, tiles per carousel.
Artwork: studio logos on/off, repository URL, genre collages on/off, plus the
last sync time and cached logo count with a manual **Sync artwork now** button.

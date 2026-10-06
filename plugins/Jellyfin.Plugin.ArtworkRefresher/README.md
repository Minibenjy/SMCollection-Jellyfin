# Artwork Refresher

Fresh posters, backdrops, logos and thumbnails for Jellyfin 10.11, from public sources, on a
schedule inside a time window, or for one item from the card's ⋮ menu. Genres, studios,
collections and Home sections get a mosaic built from artwork the server already holds.

Status: **1.1.0 release candidate, built and unit tested, not yet run on a live server.**

## What it does

- **Schedule.** One scheduled task ("Refresh artwork") wakes every 30 minutes. It works only when
  the saved next-eligible time has passed and the clock is inside the window (default 02:00–06:00,
  may cross midnight, any time zone, daylight-saving safe). Default interval 24 h. A run stops at
  the window end or after the longest-run limit and continues in the next one.
- **One item.** "Refresh artwork" in the ⋮ menu of a card, or `POST /ArtworkRefresher/Item/{id}`. It ignores the window.
- **Sources** (all optional, each with its own rate limit): TMDb, Fanart.tv, Wikimedia Commons,
  Open Library, Cover Art Archive, Google Programmable Search (off by default, official API only,
  always last). The order is per item kind and configurable. Sources are asked by the ids the item
  already has; a text search happens only when there is no id, and only an exact title is accepted.
- **Categories.** Genres and music genres: local mosaic. Studios: TMDb company logo, then Commons,
  then mosaic. Collections: TMDb, then mosaic. Mosaics use only images already in the library, are
  deterministic (same inputs, same image, nothing regenerated) and only content that is safe for
  everybody under the rules you set.
- **Home.** `GET /ArtworkRefresher/Home/Sections` and `/Home/{key}/Image`: a mosaic per library built per user
  from what that user can see. Other Home plugins read it over HTTP; there is no reference between
  plugins.

## Rotating images (1.1)

Off by default. Per image type (Primary, Backdrop, Logo, Thumb) choose **Off**, **Per page load** or **Once a day**, and the pool size (default 5).

- **Pool.** The sources are asked once per item and slot; the best N candidates are kept (addresses only, no download) and searched again after the configured days. Images are downloaded when first shown (per page load) or when chosen (daily) and cached in the plugin data folder up to a size limit.
- **Draw.** Random among the images not used since the history started; the image showing now is never drawn again; when the pool is used up the history starts again. The history is kept per slot.
- **Per page load.** `GET /ArtworkRefresher/Rotating/{id}/{type}` serves one image of the pool. An image tag cannot send a header, so the authenticated "available" call below hands out a signed token (HMAC, per item and type, valid two hours) that goes in the address together with a per-page-view nonce: ids cannot be guessed, anonymous visitors cannot make the server download anything, and the preload and the display of the same address show the same image. The client script asks `POST /ArtworkRefresher/Rotating/Available` which items rotate and swaps the address of card and detail-page images pointing at `/Items/{id}/Images/{type}`; if the endpoint has nothing the original image stays. It needs only the script this plugin already injects, so it works on the standard Home (Recently added, Continue watching...), detail pages and any Home that renders normal item images. The image stored in Jellyfin is never changed in this mode.
- **Daily.** During the scheduled run each slot whose last rotation was not today changes to the next draw (stored through Jellyfin's pipeline; replaces the current image, so try it with Dry run first). The run summary and the state file count real changes. Before the first replacement the image the item had is copied to the plugin data folder (setting on by default), and the plugin remembers a hash of what it wrote: if somebody replaces that image later, the rotation leaves it alone. ` + "`POST /ArtworkRefresher/Rotating/Restore/{itemId}/{type}`" + ` (administrator) puts the original back and locks that image type of the item.
- **State.** Pools can be large, so they live in their own file (` + "`pools.json`" + `) next to ` + "`state.json`" + `, written at the end of a run and at most every two minutes otherwise.
- **Limits.** The client part rewrites ordinary image addresses (` + "`<img src>`" + ` and CSS backgrounds that point at ` + "`/Items/{id}/Images/{type}`" + `). Images that other clients or Homes build in another way (native apps, canvas, lazy ` + "`data-src`" + `) keep their normal image: per-page-load rotation is a jellyfin-web feature.
- Locks, excluded libraries, Jellyfin's lock and book/Auto Thumbnails protection apply to rotation too. Rotation never runs in dry run.

## Recently added and new items (1.1)

Runs handle items added in the last N days first. The plugin also listens to `ILibraryManager.ItemAdded`: a new item is queued, and after a delay (so Jellyfin's own refresh finishes) its artwork, and its metadata gaps when that is on, are filled; by default only inside the time window, otherwise it waits in the queue.

## Fill missing metadata (1.1)

Opt-in. Movies, series, episodes and people with empty overview (biography), genres, year, community rating, studios or tagline get **only the empty fields** filled from TMDb, using the TMDb or IMDb id the item already has (never a name search). Fields with a value and fields locked in Jellyfin are not touched; books, comics and magazines are never touched. Dry run and the run report show how many fields would be / were filled. The state file records which fields the plugin wrote (with a short hash of the value).

## Living with Auto Thumbnails and other plugins

- Separate from Auto Thumbnails on purpose (that one extracts offline, this one uses the network).
- The primary image of books, comics and magazines is **protected by default** and so is the
  primary image of any item carrying a `ProviderIds["AutoThumbnails"]` marker. Excluded libraries, items locked in
  Jellyfin, and your own library/item image locks are never touched. Order of the checks: excluded
  library → Jellyfin lock → library lock → item lock → book protection → kind/type → "only if missing".
- Images are always saved through Jellyfin's pipeline with `saveLocallyWithMedia=false`: **no file is ever
  written next to your media**. The new image is downloaded, decoded and validated before anything is
  replaced, so a failed download never leaves an item without a cover.
- Works on a plain Jellyfin. Other plugins (File Transformation, JS Injector, Discover Home, Home Screen
  Sections, Kids/Mature…) are only detected to be shown on the page; nothing depends on them. Kids/Mature
  rules are not guessed: list their libraries and tags under "Shared images".

## Setup

1. Install, restart, open Dashboard → Plugins → Artwork Refresher.
2. Add the keys you want (TMDb is the most useful). Keys stay on the server and are never sent back to the browser.
3. Leave **Dry run** on for a first pass and read the log. Then switch it off.
4. Review libraries: exclude what must not change, tick sensitive libraries/tags for shared images.

## Safety

https only; per-provider host allow-list checked on every redirect; private, loopback, link-local and
metadata addresses refused even after DNS resolution; size, type and decoding checks; Retry-After honoured; keys never logged.
Non-administrators can refresh one item they can see only if you allow it (fill-missing only, with a cooldown).

## Licences of the sources

TMDb needs attribution ("This product uses the TMDB API but is not endorsed or certified by TMDB.") and is free for
non-commercial use. Fanart.tv: use your own keys. Wikimedia Commons: each file has its own licence; non-free files are
skipped. Cover Art Archive: use at your own responsibility. Google: a rights filter does not make an image free to reuse.

## Not in 1.0.0

TheTVDB, Comic Vine, TheAudioDB, IGDB, tags and games, Home rows beyond library mosaics, a visual preview.

## Tests

`dotnet run --project tests/Jellyfin.Plugin.ArtworkRefresher.TestHarness -c Release` runs 200+ checks: time window (midnight, DST),
lock precedence, source order, rate limit and Retry-After, SSRF guard and redirects, size limits, every source parser against
fixtures, image validation and mosaics, key masking, state file. What it cannot test without a server: the Jellyfin library
queries, SaveImage, the scheduled task trigger and the card menu on a real install.

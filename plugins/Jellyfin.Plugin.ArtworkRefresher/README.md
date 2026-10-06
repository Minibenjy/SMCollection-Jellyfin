# Artwork Refresher

Fresh posters, backdrops, logos and thumbnails for Jellyfin 10.11, from public sources, on a
schedule inside a time window, or for one item from the card's ⋮ menu. Genres, studios,
collections and Home sections get a mosaic built from artwork the server already holds.

Status: **1.0.0 release candidate, built and unit tested, not yet run on a live server.**

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

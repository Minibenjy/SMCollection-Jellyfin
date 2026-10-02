# Changelog

All notable changes to this collection are recorded here. Each plugin carries its
own version; a release tag covers whatever changed since the previous one.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

## [1.2.2] - 2026-10-02

Versions: Discover Home 0.2.2, AI Assistant 0.1.2, Kids Mode 0.1.2, Mature Content 0.1.2, Auto Thumbnails 1.0.1, Enhanced PDF Reader 1.0.1.

### Discover Home: genre/studio carousel swipe on mobile

- Swiping the genre or studio carousel on a touch screen no longer chains to the
  page (overscroll-behavior-x: contain, touch-action: pan-x pan-y), which reloaded Home.

### All plugins: no site-specific defaults, English UI

- Discover Home: badge, search, genre/studio and "Customize home" strings now follow
  the interface language (English default, Spanish included) instead of being fixed
  Spanish. The badge label defaults are empty (= localised built-in text); any value
  already saved in a server's configuration keeps working untouched.
- AI Assistant: the endpoint placeholder is the generic http://localhost:11434
  instead of a LAN address.
- Admin pages of Mature Content, Kids Mode, Auto Thumbnails and Enhanced PDF Reader:
  leftover Spanish labels translated to English to match the rest of the UI.

### Discover Home 0.2.1 — no centered search on mobile

- The centered search box only shows above the sidebar breakpoint (desktop).
  Below it the header is left exactly as Jellyfin draws it — its own search
  icon, Home/Favourites buttons and tabs — instead of squeezing the box in next
  to the header icons.

### Discover Home 0.2.0 — personalisation and sidebar

- "Customize home" entry in the sidebar (Usuario group) opens a panel where each
  user overrides the presentation defaults for their own account: large cards,
  type pills, genre and studio carousels, row shuffling, pinned sidebar and
  accent colour. Stored in Jellyfin's per-user DisplayPreferences, so it follows
  the account across devices; the administrator's configuration stays the
  default and "Reset" returns to it. Can be switched off by the administrator.
- Sidebar groups (Media, Jellyfin Enhanced, Administration, User) fold and unfold
  from their header, with a chevron. Each browser remembers which were folded.
  Administrator option, on by default.
- Genre and studio carousels get the same prev/next chevrons as Jellyfin's own
  rows, plus click-and-drag scrolling with the mouse. A drag never opens the
  tile it started on.

### Kids Mode 0.1.1, Mature Content 0.1.1, AI Assistant 0.1.1 — refreshed icons

- Kids "K" and Mature "M": cleaner geometric monograms in the same rounded tile.
  When the mode is on the tile now fills with its colour (green / red) instead of
  relying on a glow alone, and the buttons get a keyboard focus ring.
- AI Assistant launcher: same round accent button with a subtle sheen and a
  speech bubble with a spark, replacing the generic "forum" icon.

### Discover Home 0.1.3 — large cards on desktop

- New "Large cards on desktop" option, on by default. Above the sidebar
  breakpoint, poster rows are sized from Jellyfin's own landscape card (Continue
  Watching, My Media): posters at ~0.71 of its width (237×356 on a 1920px
  screen, up from 130×168), and genre/studio tiles at exactly its width and
  16:9 shape. Everything is derived from the same vw value Jellyfin uses, so it
  stays proportional at any desktop resolution. Narrow screens keep the compact
  sizes.

### Discover Home 0.1.2 — fixes

- Hides the permanent "Loading..." KefinTweaks leaves at the bottom of Home when
  its discovery engine is switched off. Its stylesheet forces that text visible
  until the page is marked discovery-ready, which never happens with discovery
  disabled. KefinTweaks' own behaviour is untouched once discovery is enabled.

### Discover Home 0.1.1 — fixes

- Studio logos now actually appear. 0.1.0 only attached the logo once it had
  loaded, but a lazy image outside the document is never fetched, so no logo was
  ever shown. The client now asks the new `/DiscoverHome/Art/Studios` endpoint
  which studios have a cached logo and only requests those, which also removes a
  404 per uncovered studio. Logo tiles are sized 16:9 at the height of the other
  tiles instead of growing to the thumbnail's natural width.
- Genre and studio carousels no longer disappear after returning to Home a few
  times: ordering state is kept per home screen instead of globally.
- Pinned rows (My Media, Continue Watching, Next Up) keep their place even when
  they finish loading after other rows.
- Genre and studio tiles link to Jellyfin's own genre/studio listing instead of a
  free-text search, and are real links (middle-click and keyboard work). Both
  lists are shuffled per session instead of always showing the first names
  alphabetically, and studios with a logo are shown first.
- "Sync artwork now" on the config page now updates the logo count and last sync
  time, which only the scheduled task did before. Concurrent syncs are serialized.
- The script tag's cache-busting version follows the assembly version instead of
  being maintained by hand.

### Discover Home 0.1.0 — new plugin

- Reworks the web client's home screen into a discovery front page: the navigation
  drawer pinned open on desktop, a centered search box with live suggestions from
  `/Search/Hints`, denser poster cards, and a CSS-only media-type pill.
- Genre and studio carousels shuffled in between Jellyfin's own rows, at a random
  gap and never the same type twice running. Genre tiles are backed by a collage of
  posters already in that genre; studio tiles use a real logo when one is cached.
- Daily scheduled task downloads studio logos from a public artwork repository, for
  the studios this library actually contains. Genres are deliberately not
  downloaded — no public source exists, so those are composed in the browser.
- Administrator configuration page covering layout, cards, rows and artwork,
  including a switch that removes the client script again without uninstalling.
- Presentation only: it reorders and restyles what the server already rendered for
  a user, and changes nothing about what they are permitted to see.

## [1.0.0] — First public release

First packaging of five plugins that had been running privately on a single
Jellyfin 10.11 server.

### AI Assistant 0.1.0

- Chat panel in the web client, with per-user provider routing. Ollama implemented;
  OpenAI-compatible, Anthropic and OpenRouter adapters planned.
- Sixteen tools covering search, browsing, item details, episode listing and
  sampling, continue-watching, playlist create/read/edit/rename/delete, watched and
  favourite state, and administrator-only collection creation.
- Recommendation by plot, decade or cast: `person` and `year_from`/`year_to` filters,
  with an automatic fallback that drops an over-specified filter rather than
  returning nothing.
- Every state-changing tool is confirmed by the user first, and the confirmation
  reports the **resolved** item count, so an id that expands to a whole series cannot
  be approved as "1 item".
- Guardrails aimed at small local models: automatic query broadening, loud failure on
  unknown filter values, repeated-identical-call detection, recovery of tool calls a
  model wrote into its reply text, and honest reporting of what a write actually did.

### Auto Thumbnails 1.0.0

- Covers for books, comics and magazines from CBZ/CBR/CB7/CBT/PDF/EPUB; video frames
  via ffmpeg; folder images from the first child that has one.
- Runs from library scans, from the plugin page with live progress, or from a daily
  scheduled task — all sharing one job service.
- Never overwrites existing artwork by default. Images are written to
  `/config/metadata`, never into your media folders.
- pdfium is now resolved for the running OS and architecture, so PDF covers work on
  Linux, Windows and macOS rather than Linux only.

### Enhanced PDF Reader 1.0.0

- Full reader replacing the built-in viewer: continuous scroll, zoom and fit modes,
  go-to-page, rotate, and a 3D page-flip book mode.
- Reading position is per user and stored on the server, and is mirrored into
  Jellyfin's native user data so PDFs appear in **Continue Reading**.

### Kids Mode 0.1.0

- Per-account allow-list mode with a topbar toggle, backed by `AllowedTags` and
  library restrictions, restoring the previous policy exactly when switched off.
- A global curated list plus per-account add/remove overrides.

### Mature Content 0.1.0

- Mark items, folders or whole libraries with native `mature` / `+18` tags and hide
  them via each user's `BlockedTags`.
- Per-account control of both the toggle and default visibility.
- Durable mark history, re-applied at startup and after every library scan.

[Unreleased]: https://github.com/Minibenjy/SMCollection-Jellyfin/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/Minibenjy/SMCollection-Jellyfin/releases/tag/v1.0.0

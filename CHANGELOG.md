# Changelog

All notable changes to this collection are recorded here. Each plugin carries its
own version; a release tag covers whatever changed since the previous one.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

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

# Enhanced PDF Reader

Replaces the minimal PDF viewer in the Jellyfin web client — which turned pages
only with the arrow keys or by tapping the edges, with no buttons and no page
indicator — with a full reader.

## Features

- **Continuous scroll** through the whole document, rendering lazily so a
  400-page file does not exhaust memory.
- **Book mode**: a 3D page-flip using [StPageFlip](https://github.com/Nodlik/StPageFlip),
  two pages side by side in landscape and one in portrait, with corner dragging and
  swipe. Toggle with the `auto_stories` button or the `m` key; the choice is
  remembered per browser.
- **Zoom**: −/+ buttons, **fit width**, **fit page**, percentage readout.
- **Go to page** — the `12 / 210` readout is editable; type and press Enter.
- Previous/next page buttons, **rotate 90°**, and download.
- **Per-user reading position**, stored on the server (see below).
- **Keyboard**: `←/↑/PageUp/k` previous · `→/↓/PageDown/Space/j` next ·
  `Home/End` · `+/−` zoom · `w` fit width · `p` fit page · `r` rotate · `Esc` close.
- A `menu_book` button on the detail page of every PDF.

## Reading position and Continue Reading

Reading position is **per user and stored on the server**, not in the browser. An
earlier version kept it in `localStorage`, which meant every account sharing a
browser also shared its place in every book.

- `ProgressStore` keeps a JSON map of user → item → `{ Page, NumPages, UpdatedUtc }`
  in the plugin's own data directory.
- `GET|POST /EnhancedPdfReader/Progress/{itemId}` read and write it, both
  `[Authorize]`. The user id comes from `IAuthorizationContext`, never from the
  client — a `?userId=` parameter is honoured only for an API key with no user
  attached.
- The client saves with a 1.5 s debounce and again when the tab is hidden or closed,
  and keeps `localStorage` only as an offline cache keyed by *both* user and item.

Progress is also **mirrored into Jellyfin's native user data**, so PDFs show up in
the web client's own **Continue Reading** row: ticks are set to
`RunTimeTicks * page / numPages`, and reaching the last page marks the book played
and clears the position, so it drops off the row.

Zoom level and reading mode stay browser-local on purpose — those are a property of
the screen you are reading on, not of the account.

## How it works

Jellyfin's media players live in the **web client**, not in server plugins, so a
`.dll` cannot register a player directly. This plugin uses the same pattern
Jellyscrub does:

1. `ScriptInjectionHostedService` inserts
   `<script src="../EnhancedPdfReader/ClientScript">` before `</body>` in
   `jellyfin-web/index.html` at every startup, so it survives Jellyfin image
   updates. It also removes its own older tags rather than accumulating them.
   This can be turned off in the plugin configuration.
2. `EnhancedPdfReaderController` serves the reader script and an embedded copy of
   **pdf.js 4.7.76** (`/EnhancedPdfReader/pdf.mjs`, `/pdf.worker.mjs`) and
   StPageFlip — no CDN, nothing fetched from outside your server.
3. `enhancedPdfReader.js` patches `fetch`/`XMLHttpRequest` to capture the PDF's
   download URL, detects the built-in viewer opening (`#pdfPlayer`), closes it and
   opens the full reader in its place.

## Configuration

Dashboard → Plugins → Enhanced PDF Reader. The only setting is whether to inject
the client script; turn it off and the plugin does nothing visible.

## Known limitations

- Jellyfin has no extension point for registering a media player, so this works by
  script injection. If a future Jellyfin release changes `index.html` handling or the
  `#pdfPlayer` element, this plugin will need updating.
- Browsers cache the injected script aggressively. The script tag carries a version
  query string and is served `Cache-Control: no-store`, but after an upgrade a hard
  reload (Ctrl+Shift+R) is sometimes still needed.

## Third-party components

[pdf.js](https://mozilla.github.io/pdf.js/) (Apache-2.0),
[StPageFlip](https://github.com/Nodlik/StPageFlip) (MIT). Both are embedded in the
assembly and served from your own server.

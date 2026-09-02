# Script injection into the web client

Four plugins in this collection add UI to the Jellyfin web client: the AI Assistant,
Enhanced PDF Reader, Kids Mode and Mature Content. This page explains how, because it
involves a plugin writing to a file inside your Jellyfin installation, and you should
know that before installing.

## Why it is done this way

Jellyfin's server plugin API has **no supported extension point for client-side UI**.
A `.dll` can add API endpoints, scheduled tasks, metadata providers and a
configuration page in the dashboard — but it cannot add a button to the player, a
toggle to the topbar, or a new media viewer.

The community's answer, used by Jellyscrub and others, is to inject a `<script>` tag
into `jellyfin-web/index.html` pointing at an endpoint the plugin serves. That is what
these plugins do.

## What actually happens

At server startup, each plugin's `ScriptInjectionHostedService`:

1. Locates `jellyfin-web/index.html`.
2. Removes any of **its own** previous tags — so upgrades replace rather than
   accumulate.
3. Inserts `<script src="../<Plugin>/ClientScript?v=<version>"></script>` before
   `</body>`.

The script itself is embedded in the plugin assembly and served by the plugin's own
controller. Nothing is fetched from a CDN or from any host you did not configure.

Injection runs on **every startup** because a Jellyfin upgrade replaces
`index.html`, which would otherwise silently remove the UI.

## Consequences you should know about

- **A plugin writes to a file inside your Jellyfin web root.** If that is not
  acceptable on your setup, turn injection off in the plugin's configuration; the
  server-side half keeps working, you just lose the UI.
- **Uninstalling leaves a dead tag** until the next Jellyfin update rewrites
  `index.html`. It points at an endpoint that no longer exists, the browser gets a
  404, and nothing else happens.
- **Browsers cache the script hard.** The `?v=` query string changes with the plugin
  version to force a refetch, and the endpoint sends `Cache-Control: no-store`, but
  after an upgrade a hard reload (Ctrl+Shift+R) is sometimes still needed.
- **If `index.html` is read-only** — some container setups mount it that way —
  injection fails and is logged as a warning. The plugin still loads.

# Security

## Reporting a vulnerability

Open a [private security advisory](https://github.com/Minibenjy/SMCollection-Jellyfin/security/advisories/new)
rather than a public issue. Include the plugin, its version, your Jellyfin version,
and what an attacker gets. You will get a reply; there is no bounty.

## What you are trusting

A Jellyfin plugin is a .NET assembly loaded **in-process by your server**. It runs
with the server's privileges: it can read your library database, reach your
filesystem, and open network connections. That is true of every Jellyfin plugin,
including these.

These plugins were written with heavy AI assistance and have **not been audited by
a third party**. The source is all here under GPL-3.0. If your server holds
anything that matters, read the code before installing.

## What each plugin touches

| Plugin | Reads | Writes | Network |
|---|---|---|---|
| **AI Assistant** | Library metadata, as the acting user | Playlists, watched/favourite state, its own encrypted credential store | **Yes** — to the AI provider each user configures |
| **Auto Thumbnails** | Media files, to extract a page or frame | Images into `/config/metadata` only | No |
| **Enhanced PDF Reader** | Nothing beyond the PDF being read | `jellyfin-web/index.html`, its own progress file, native user data | No |
| **Kids Mode** | User policies | User policies, item tags, its own config | No |
| **Mature Content** | User policies | User policies, item tags, its own config | No |

Only the AI Assistant makes outbound connections, and only to the endpoint the user
configured. Nothing here phones home, collects telemetry, or contacts any service
the operator did not set up.

## Notes worth knowing

**Script injection into the web client.** Four of these plugins add a `<script>` tag
to `jellyfin-web/index.html`, because Jellyfin has no supported extension point for
client-side UI. This is the standard community pattern (Jellyscrub and others do the
same), but it does mean these plugins **modify a file inside your Jellyfin
installation**. Each re-injects at startup and removes its own stale tags. It can be
disabled per plugin in configuration. See [docs/SCRIPT-INJECTION.md](docs/SCRIPT-INJECTION.md).

**Kids Mode and Mature Content are not security boundaries.** They control what the
Jellyfin UI shows, using Jellyfin's own permission system. Someone with filesystem
access, or with the file paths and a direct HTTP client, is a different problem that
no plugin in this collection solves.

**The AI Assistant is the one with a real attack surface**, because it processes
untrusted text (library metadata written by external scrapers) with a language model
and gives that model tools. Its authorization boundary, prompt-injection handling and
credential storage are documented separately in
[plugins/Jellyfin.Plugin.AiAssistant/SECURITY.md](plugins/Jellyfin.Plugin.AiAssistant/SECURITY.md).
The short version: every tool executes as the acting user through Jellyfin's own
query layer, no tool accepts a user id, and anything that writes requires the person
to approve it first.

**Auto Thumbnails parses untrusted files** — PDFs and archives from your own library —
using PDFium, SharpCompress and ImageSharp. A malicious file crashing one of those is
the realistic worst case; extraction runs in-process, so a parser vulnerability in a
dependency is a vulnerability here. Dependencies are pinned and updated deliberately.

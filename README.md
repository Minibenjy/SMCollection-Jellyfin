# SMCollection for Jellyfin

Five plugins for Jellyfin 10.11, installable as a single repository: a
bring-your-own-provider AI assistant, automatic thumbnails for anything that has
none, a real PDF reader, a kid-safe allow-list mode, and a mature-content toggle.

They were written to solve concrete problems on one home server — comics with no
covers, a PDF viewer you could not page through, an account a child could use
without stumbling into the rest of the library — and then cleaned up so anyone
else can install them.

---

> ### Built with AI assistance
>
> **These plugins were "vibe coded": designed and written largely by an AI
> assistant (Claude), directed and tested by a human on a live Jellyfin server.**
>
> What that means in practice, stated plainly so you can decide for yourself:
>
> - Every plugin has been **run on a real 10.11 server** with a real library, and
>   the behaviour described in each README is behaviour that was observed, not
>   assumed. Where something does not work, it says so.
> - There is **no formal test suite**. Two plugins ship a test harness, the rest
>   were verified by hand against a live server.
> - The code has **not been audited by a third party**. It is GPL-3.0 and the
>   source is all here — if you run a server that matters to you, read it before
>   you install it. That is good advice for any plugin, and better advice for
>   this one.
> - Plugins run **in-process with your Jellyfin server** and have the access that
>   implies. See [SECURITY.md](SECURITY.md) for what each one touches.
>
> Issues and pull requests are welcome, including "this whole approach is wrong".

---

## The plugins

| Plugin | What it does | Status |
|---|---|---|
| [AI Assistant](plugins/Jellyfin.Plugin.AiAssistant/) | A chat panel in the web client. Each user brings their own AI provider; the assistant searches, recommends from real metadata, and builds playlists strictly within that user's permissions. | Beta |
| [Auto Thumbnails](plugins/Jellyfin.Plugin.AutoThumbnails/) | Thumbnails for whatever no scraper covered — first page of a CBZ/CBR/PDF/EPUB, a video frame, a folder's first child. Never overwrites existing artwork. | Stable |
| [Enhanced PDF Reader](plugins/Jellyfin.Plugin.EnhancedPdfReader/) | Replaces the minimal built-in PDF viewer with a real reader: scroll, zoom, go-to-page, rotate, page-flip book mode, and per-user reading position that feeds Continue Reading. | Stable |
| [Kids Mode](plugins/Jellyfin.Plugin.KidsMode/) | Turns any account into a restricted allow-list view with one toggle, and restores the previous policy exactly when switched off. | Beta |
| [Mature Content](plugins/Jellyfin.Plugin.MatureContent/) | Marks items or whole libraries as mature using native tags and hides them behind a topbar toggle, per account. | Beta |

"Beta" means it works and is in daily use, but the surface is still moving.

## Requirements

- **Jellyfin 10.11.x** (`targetAbi` 10.11.0.0). Nothing here is a fork or a patched
  web client — it all runs on a stock server.
- Auto Thumbnails uses **ffmpeg** for video frames, which your Jellyfin install
  already has.
- The AI Assistant needs an **AI provider you supply** (a local Ollama, or any
  OpenAI-compatible endpoint). Nothing is sent anywhere you did not configure.

## Install

### As a plugin repository (recommended)

This installs and updates every plugin from the Jellyfin dashboard.

1. **Dashboard → Plugins → Repositories → `+`**
2. Name: `SMCollection`
3. URL:

   ```
   https://raw.githubusercontent.com/Minibenjy/SMCollection-Jellyfin/main/manifest.json
   ```

4. **Dashboard → Plugins → Catalog**, pick what you want, install, restart Jellyfin.

### Manually

Download a zip from [Releases](https://github.com/Minibenjy/SMCollection-Jellyfin/releases),
extract it into a new folder under your Jellyfin `plugins` directory, and restart:

```
<jellyfin-config>/plugins/Auto Thumbnails_1.0.0.0/
    Jellyfin.Plugin.AutoThumbnails.dll
    ...
```

The folder name is not significant to Jellyfin, but `Name_Version` is the
convention the server itself uses.

### From source

```sh
git clone https://github.com/Minibenjy/SMCollection-Jellyfin.git
cd SMCollection-Jellyfin
./build.sh
```

Requires the .NET 9 SDK and `zip`. Artifacts land in `artifacts/`. See
[docs/BUILDING.md](docs/BUILDING.md) for details, including why packaging uses
`dotnet publish` rather than `dotnet build`.

## Uninstalling

Remove the plugin from the dashboard, or delete its folder and restart.

Two plugins modify the web client's `index.html` to inject a script — Enhanced PDF
Reader, Kids Mode, Mature Content and the AI Assistant all use this standard
pattern. Removing the plugin leaves a dead `<script>` tag until the next Jellyfin
update rewrites the file; it is harmless, and each plugin cleans up its own older
tags on startup. See [docs/SCRIPT-INJECTION.md](docs/SCRIPT-INJECTION.md).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Bug reports that include the Jellyfin
version, the plugin version and the relevant server log lines are the most useful
thing you can send.

## License

[GPL-3.0](LICENSE), matching Jellyfin itself.

The plugins bundle third-party components under their own licenses — pdf.js
(Apache-2.0), StPageFlip (MIT), PDFium (BSD-3-Clause), and the NuGet packages
listed in each plugin's project file.

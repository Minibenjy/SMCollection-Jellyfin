# Auto Thumbnails

Gives a thumbnail to **anything that does not have one**, taken from the content
itself. It never overwrites artwork that already came from a scraper (Comic Vine,
TheMovieDb, AniDB…) or from a local file: if an item already has an image, this
plugin leaves it alone.

## What it covers

| Item type | Where the thumbnail comes from |
|---|---|
| Books, comics, magazines | First page of `.cbz` `.cbr` `.cb7` `.cbt` `.pdf` `.epub` |
| Movies, episodes, videos | A frame via ffmpeg, at 10% of the duration by default |
| Series, seasons, collections | The image of the first child that has one |

**Page 0 is the first page** (the cover). **Frame second 0 is the first frame** —
be aware that many videos open on black or a distributor logo, so raising that to
5–10 seconds usually gives a better result.

For EPUB, the cover declared in the manifest is honoured (EPUB 2
`<meta name="cover">` and EPUB 3 `properties="cover-image"`) before falling back
to "the first image in the archive".

Pages inside archives are sorted **naturally**, so `page2` comes before `page10`.
Plain alphabetical ordering picks the wrong cover for most scanned material.

## How it runs

There are three entry points, and all three share one `ThumbnailJobService`, so a
run started from any of them shows its progress in the others.

1. **During library scans** — `BookCoverImageProvider` is an
   `IDynamicImageProvider` with `Order = 1000`, meaning it goes last: every
   scraper gets its turn first. This covers new books and comics automatically.
2. **From the plugin page** — Dashboard → Plugins → Auto Thumbnails. A *Start*
   button with a progress bar, counters and a live log. You choose content types,
   specific libraries, and whether to fill only what is missing or regenerate
   everything.
3. **The scheduled task "Generate missing thumbnails"** — every 24 hours, using
   the saved settings.

Only one run happens at a time (`SemaphoreSlim`), and *Cancel* reaches a run that
the scheduled task started, because the cancellation tokens are linked.

## Configuration

Dashboard → Plugins → Auto Thumbnails.

| Setting | Meaning |
|---|---|
| Content types | Books, videos, folders — any combination |
| Page | Which page of a book to use. `0` is the first |
| Frame second | Where to grab a video frame. `0` is the first frame |
| PDF render resolution | Higher is sharper and slower |
| JPEG quality | Output quality for generated images |
| Overwrite existing | **Off by default.** This is what guarantees the daily task never replaces artwork you already have |

## API

All endpoints require administrator rights (`Policies.RequiresElevation`).

| Endpoint | Purpose |
|---|---|
| `GET /AutoThumbnails/Libraries` | Selectable libraries |
| `GET /AutoThumbnails/Status?since=N` | Progress, counters, and only log lines after `N` |
| `POST /AutoThumbnails/Start` | Start a run (`Books`, `Videos`, `Folders`, `Regenerate`, `LibraryIds`) |
| `POST /AutoThumbnails/Cancel` | Request a stop |

## Where the images go

Jellyfin's image pipeline is **file-based**: `ItemImageInfo.Path` has to point at
a real file, and the image controller reads and caches from disk. There is no
extension point for serving an image on demand from a stream —
`IDynamicImageProvider` returns a stream, but `ProviderManager` persists it
immediately.

What matters is *where* it is persisted: in `/config/metadata`, Jellyfin's own
internal store, **not** in your comics or video folders. Your library is not
touched, and everything generated is disposable and regenerable.

## Known limitations

- **`.djvu` is not supported.** There is no djvu reader for .NET and the Jellyfin
  container does not ship `djvulibre`. Those files will stay without covers.
- **PDFium is not thread-safe**, so pages render one at a time. A large
  regenerate-everything run over a PDF-heavy library takes a while.
- **Solid RAR archives** cannot be read entry-by-entry in random order. If
  `OpenEntryStream` fails, the extractor falls back to a forward sequential scan;
  a few malformed `.cbr` files are treated as end-of-archive rather than failing
  the whole run.

## Implementation notes

Details that cost real time to discover, kept here so they are not rediscovered:

- **Native pdfium loading.** Jellyfin loads plugins from a directory that is not on
  the native library search path, so Docnet's `DllImport("pdfium")` fails with
  `DllNotFoundException`. `PdfCoverSource.EnsureNativeResolver` installs a
  `DllImportResolver` on Docnet's assembly that probes for the right binary for the
  running OS and architecture, both flat beside the plugin and under
  `runtimes/<rid>/native/`.
- **PDF page backgrounds.** A PDF page has no background of its own; unpainted area
  is transparent and flattening it to JPEG would come out **black**. Pages are
  composited onto white before encoding.
- **NU1902 is suppressed deliberately.** The SharpCompress advisory
  (GHSA-6c8g-7p36-r338) is a zip-slip in *path-based extraction*. This plugin never
  extracts entries to disk using their stored name; it reads a single entry into
  memory. The reasoning is recorded in the `.csproj` next to the suppression.

## Third-party components

PDFium (BSD-3-Clause), [Docnet.Core](https://github.com/GowenGit/docnet) (MIT),
[SharpCompress](https://github.com/adamhathcock/sharpcompress) (MIT),
[SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) (Apache-2.0).

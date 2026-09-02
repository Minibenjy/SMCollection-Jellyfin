# Building and packaging

## Requirements

- **.NET 9 SDK**
- **`zip`** (used by `build.sh`)
- **Python 3** (used to generate `manifest.json`)

## Compile everything

```sh
dotnet build JellyfinPlugins.sln -c Release
```

This must finish with **zero warnings**. Shared settings — target framework,
nullable, analyzers, and the Jellyfin version — live in `Directory.Build.props`;
each plugin's `.csproj` carries only its own name, version and dependencies.

## Build release packages

```sh
./build.sh
```

Produces `artifacts/<PluginName>_<version>.zip` for every plugin and rewrites
`manifest.json` with the MD5 of each artifact. `./build.sh --manifest-only`
regenerates the manifest from artifacts already on disk.

## Two rules that are easy to get wrong

**Use `dotnet publish`, not `dotnet build`.** For a *library* project, `build` does
not copy NuGet dependencies to the output directory. A plugin with any dependency of
its own — Auto Thumbnails has four — builds fine and then fails to load on the server
with a `FileNotFoundException` for an assembly you never noticed was missing.

**Ship only your own dependencies.** The Jellyfin server already loads
`MediaBrowser.*`, `Jellyfin.*`, `Microsoft.*` and `System.*`. Including your own copy
is at best dead weight and at worst an assembly-version conflict at load time.
`build.sh` filters these out; the list is in `is_server_provided()` there.

## Native libraries

Auto Thumbnails depends on **PDFium** through Docnet, which ships one native binary
per platform under `runtimes/<rid>/native/`. That layout is preserved in the package,
and `PdfCoverSource.EnsureNativeResolver` picks the right one at load time for the
running OS and architecture.

This is necessary because Jellyfin loads plugins from a directory that is not on the
native library search path, so the P/Invoke fails without an explicit
`DllImportResolver`. It is also why that plugin's zip is around 19 MB — it carries
pdfium for Linux, Windows and macOS, x64 and arm64.

## Bumping the Jellyfin version

Two lines in `Directory.Build.props`:

```xml
<JellyfinControllerVersion>10.11.11</JellyfinControllerVersion>
<TargetAbi>10.11.0.0</TargetAbi>
```

`JellyfinControllerVersion` is the NuGet package everything compiles against.
`TargetAbi` is what the server checks at load time and what goes into the manifest;
it must also be updated in each `plugins/*/build.yaml`.

## Cutting a release

1. Bump versions in the plugin `.csproj` **and** `build.yaml`, and update
   `CHANGELOG.md`.
2. Commit, then tag: `git tag v1.0.0 && git push --tags`.
3. The `release` workflow builds, packages, creates the GitHub release with the zips
   attached, regenerates `manifest.json` with the real download URLs and checksums,
   and commits it back to `main`.

Servers that added the repository see the new version on their next catalogue
refresh.

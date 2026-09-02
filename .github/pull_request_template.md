## What this changes

<!-- And why. If it fixes an issue, link it. -->

## How it was tested

<!-- Which Jellyfin version, what library, what you actually exercised.
     "Not tested on a live server" is a fine answer — just say so. -->

## Checklist

- [ ] `dotnet build JellyfinPlugins.sln -c Release` passes with **zero warnings**
- [ ] Version bumped in the plugin's `.csproj` **and** `build.yaml`, if behaviour changed
- [ ] `CHANGELOG.md` updated, if behaviour changed
- [ ] Any new suppression or workaround has a comment saying why

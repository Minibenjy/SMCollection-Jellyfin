# Contributing

Issues and pull requests are welcome, including ones that say an approach is wrong.

## Reporting a bug

The three things that make a report actionable:

1. **Jellyfin version** and **plugin version**.
2. **What you expected and what happened.**
3. **The server log around the failure** — Dashboard → Logs, or
   `<config>/log/log_*.log`. Plugin log lines are prefixed with
   `Jellyfin.Plugin.<Name>`.

Please redact anything private before pasting: paths, API keys, other users' names.

## Development setup

Requires the **.NET 9 SDK** and `zip`.

```sh
git clone https://github.com/Minibenjy/SMCollection-Jellyfin.git
cd SMCollection-Jellyfin
dotnet build JellyfinPlugins.sln -c Release   # compile everything
./build.sh                                    # build + package + manifest
```

To try a change on a real server, copy the built `.dll` into the plugin's folder in
your Jellyfin config and restart. [docs/BUILDING.md](docs/BUILDING.md) covers the
packaging rules that are easy to get wrong.

## Pull requests

- **Keep the build clean.** `dotnet build JellyfinPlugins.sln -c Release` must
  produce zero warnings. If you have to suppress something, suppress it narrowly and
  write down why, next to the suppression.
- **Match the surrounding code.** These files are commented more than most: comments
  explain *why* a thing is done, especially where the obvious approach failed. Keep
  that habit — a comment that only restates the code is worse than none.
- **Say what you tested.** "Ran on 10.11.11 with a 900-item comics library" is worth
  more than a green checkbox. There is no broad automated test suite; honesty about
  what was and was not exercised is what stands in for it.
- **Bump the version** in the plugin's `.csproj` *and* its `build.yaml` when
  behaviour changes, and add a line to [CHANGELOG.md](CHANGELOG.md).

## A note on AI-assisted contributions

This repository was largely AI-written, so AI-assisted PRs are welcome on their
merits. The bar is the same either way: you understand the change, you ran it, and
you can defend it in review. Please do say when a change was largely model-generated
— not as a disclaimer, just so reviewers know where to look harder.

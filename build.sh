#!/usr/bin/env bash
#
# Builds every plugin, packages each one the way Jellyfin expects, and refreshes
# manifest.json so the repository can be added to a server as a plugin source.
#
#   ./build.sh                 build and package into ./artifacts
#   ./build.sh --manifest-only regenerate manifest.json from existing artifacts
#
# The base URL the manifest points at is taken from GITHUB_REPOSITORY when running
# in Actions, and falls back to the origin remote so a local run produces the same
# thing a release would.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARTIFACTS="$ROOT/artifacts"
CONFIG="${CONFIG:-Release}"

# Assemblies the Jellyfin server already loads. Shipping our own copy of these is
# at best dead weight and at worst an assembly-version conflict at load time, so a
# packaged plugin contains its own DLL plus the dependencies Jellyfin does not have.
is_server_provided() {
  case "$1" in
    Jellyfin.Plugin.*) return 1 ;;
    MediaBrowser.*|Jellyfin.*|Emby.*|Microsoft.*|System.*|netstandard.dll) return 0 ;;
    BitFaster.Caching.dll|Diacritics.dll|ICU4N*.dll|J2N.dll|NEbml.*.dll|Polly*.dll) return 0 ;;
    *) return 1 ;;
  esac
}

version_of() { grep -oP '(?<=<Version>)[^<]+' "$1" | head -1; }

package_one() {
  local dir="$1" name staging version
  name="$(basename "$dir")"
  version="$(version_of "$dir/$name.csproj")"
  staging="$(mktemp -d)"

  echo "==> $name $version"

  # publish, not build: for a library project, `dotnet build` does not copy NuGet
  # dependencies to the output directory, so a plugin with any dependency of its own
  # ends up shipping without it and fails to load on the server.
  dotnet publish "$dir" -c "$CONFIG" -o "$staging/publish" --nologo -v quiet

  mkdir -p "$staging/plugin"
  local kept=0
  for f in "$staging/publish"/*.dll; do
    [ -e "$f" ] || continue
    local base; base="$(basename "$f")"
    if is_server_provided "$base"; then continue; fi
    cp "$f" "$staging/plugin/"
    kept=$((kept + 1))
  done

  # Native binaries keep their runtimes/<rid>/native layout: the plugin resolves them
  # by RID at load time, and flattening would break every platform but one.
  if [ -d "$staging/publish/runtimes" ]; then
    cp -r "$staging/publish/runtimes" "$staging/plugin/"
  fi

  cp "$ROOT/LICENSE" "$staging/plugin/"
  [ -f "$dir/README.md" ] && cp "$dir/README.md" "$staging/plugin/"

  # Jellyfin writes meta.json itself when installing from a repository, but a manual
  # install has nothing to write it from, and without it the dashboard cannot report
  # the plugin's version or status. Generating it from build.yaml keeps the two from
  # drifting apart, which a hand-maintained copy in the source tree would not.
  local assemblies
  assemblies="$(cd "$staging/plugin" && ls *.dll 2>/dev/null | tr '\n' ' ')"
  META="$dir/build.yaml" ASSEMBLIES="$assemblies" \
    python3 "$ROOT/tools/make-meta.py" > "$staging/plugin/meta.json"

  mkdir -p "$ARTIFACTS"
  local zip="$ARTIFACTS/${name}_${version}.zip"
  rm -f "$zip"
  (cd "$staging/plugin" && zip -qr "$zip" .)
  rm -rf "$staging"

  echo "    $kept assembly(ies) -> $(basename "$zip")"
}

base_url() {
  if [ -n "${GITHUB_REPOSITORY:-}" ]; then
    echo "https://github.com/${GITHUB_REPOSITORY}"
  else
    git -C "$ROOT" remote get-url origin 2>/dev/null \
      | sed -e 's|git@github.com:|https://github.com/|' -e 's|\.git$||' \
      || echo "https://github.com/Minibenjy/SMCollection-Jellyfin"
  fi
}

if [ "${1:-}" != "--manifest-only" ]; then
  rm -rf "$ARTIFACTS"
  for dir in "$ROOT"/plugins/*/; do
    package_one "${dir%/}"
  done
fi

echo "==> manifest.json"
BASE_URL="$(base_url)" TAG="${RELEASE_TAG:-}" python3 "$ROOT/tools/make-manifest.py"
echo "Done."

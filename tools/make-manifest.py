#!/usr/bin/env python3
"""Regenerate manifest.json from the packaged artifacts.

Jellyfin reads a repository manifest to decide what a server may install and which
version it already has. Every published zip has to appear here with the exact MD5 the
server will compute after downloading it, or the install fails with a checksum error
that says nothing useful — so the checksum is always taken from the artifact on disk
rather than written by hand.

Existing entries are preserved: a rebuild adds the new version to the front of a
plugin's version list instead of replacing the history, which is what lets a server
roll back to an earlier release from the dashboard.
"""

from __future__ import annotations

import hashlib
import json
import os
import pathlib
import re
import sys
from datetime import datetime, timezone

ROOT = pathlib.Path(__file__).resolve().parent.parent
ARTIFACTS = ROOT / "artifacts"
MANIFEST = ROOT / "manifest.json"

BASE_URL = os.environ.get("BASE_URL", "").rstrip("/")
TAG = os.environ.get("TAG", "").strip()


def read_build_yaml(path: pathlib.Path) -> dict:
    """Read the handful of scalar keys we need without pulling in a YAML dependency."""
    text = path.read_text(encoding="utf-8")
    out: dict[str, str] = {}
    for key in ("name", "guid", "version", "targetAbi", "category", "overview", "owner"):
        m = re.search(rf'^{key}:\s*"([^"]*)"\s*$', text, re.M)
        if m:
            out[key] = m.group(1)

    block = re.search(r"^description: >\n((?:  .*\n)+)", text, re.M)
    out["description"] = " ".join(block.group(1).split()) if block else out.get("overview", "")
    return out


def md5(path: pathlib.Path) -> str:
    digest = hashlib.md5()  # noqa: S324 - Jellyfin's manifest format specifies MD5.
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    existing = {}
    if MANIFEST.exists():
        for entry in json.loads(MANIFEST.read_text(encoding="utf-8")):
            existing[entry["guid"]] = entry

    manifest = []
    now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

    for plugin_dir in sorted((ROOT / "plugins").iterdir()):
        meta_path = plugin_dir / "build.yaml"
        if not meta_path.is_file():
            continue

        meta = read_build_yaml(meta_path)
        version = meta["version"]
        zip_path = ARTIFACTS / f"{plugin_dir.name}_{version}.zip"

        entry = existing.get(meta["guid"], {})
        versions = entry.get("versions", [])

        if zip_path.is_file():
            tag = TAG or f"v{version}"
            new_version = {
                "version": version,
                "changelog": f"See https://github.com/Minibenjy/SMCollection-Jellyfin/blob/main/CHANGELOG.md",
                "targetAbi": meta["targetAbi"],
                "sourceUrl": f"{BASE_URL}/releases/download/{tag}/{zip_path.name}",
                "checksum": md5(zip_path),
                "timestamp": now,
            }
            versions = [v for v in versions if v["version"] != version]
            versions.insert(0, new_version)
        elif not versions:
            print(f"  ! {plugin_dir.name}: no artifact and no previous version, skipping")
            continue

        manifest.append(
            {
                "guid": meta["guid"],
                "name": meta["name"],
                "description": meta["description"],
                "overview": meta["overview"],
                "owner": meta["owner"],
                "category": meta["category"],
                "imageUrl": f"{BASE_URL}/raw/main/docs/icons/{plugin_dir.name}.png",
                "versions": versions,
            }
        )
        print(f"  {meta['name']:<22} {version}  {len(versions)} version(s)")

    MANIFEST.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())

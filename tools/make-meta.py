#!/usr/bin/env python3
"""Emit the meta.json that goes inside a plugin package, derived from its build.yaml.

Jellyfin creates this file itself when it installs a plugin from a repository. A
manual install has nothing to create it from, and a plugin folder without meta.json
loads but reports no version in the dashboard — so it is generated at package time
rather than kept by hand, where it would drift from build.yaml.
"""

from __future__ import annotations

import json
import os
import pathlib
import re
import sys
from datetime import datetime, timezone

meta_path = pathlib.Path(os.environ["META"])
text = meta_path.read_text(encoding="utf-8")

# The managed assemblies, passed in by build.sh.
#
# This list is not cosmetic. With it empty, Jellyfin's plugin manager falls back to
# enumerating *.dll recursively through the plugin folder and loading every hit as a
# managed assembly. A plugin that ships a native Windows binary under
# runtimes/win-x64/native/pdfium.dll therefore has that binary handed to
# Assembly.LoadFrom, which throws, and the whole plugin is disabled with
# "Failed to load assembly ... Disabling plugin". Naming the real assemblies here
# stops the scan and makes shipping cross-platform natives possible at all.
assemblies = [a for a in os.environ.get("ASSEMBLIES", "").split() if a]


def scalar(key: str, default: str = "") -> str:
    match = re.search(rf'^{key}:\s*"([^"]*)"\s*$', text, re.M)
    return match.group(1) if match else default


block = re.search(r"^description: >\n((?:  .*\n)+)", text, re.M)
description = " ".join(block.group(1).split()) if block else scalar("overview")

json.dump(
    {
        "category": scalar("category", "General"),
        "changelog": scalar("changelog"),
        "description": description,
        "guid": scalar("guid"),
        "name": scalar("name"),
        "overview": scalar("overview"),
        "owner": scalar("owner"),
        "targetAbi": scalar("targetAbi"),
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.0000000Z"),
        "version": scalar("version"),
        "status": "Active",
        "autoUpdate": True,
        "imagePath": None,
        "assemblies": assemblies,
    },
    sys.stdout,
    indent=2,
)
sys.stdout.write("\n")

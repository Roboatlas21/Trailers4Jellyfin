#!/usr/bin/env python3
"""
Updates manifest.json with a new version entry after a release build.
Usage: python3 update_manifest.py <version> <checksum>
  version  - e.g. 1.0.0.0
  checksum - MD5 hex of the release zip
"""
from __future__ import annotations

import json
import os
import sys
import datetime
import yaml


MANIFEST_FILE = "manifest.json"
BUILD_YAML = "build.yaml"
REPO = os.environ.get("GITHUB_REPOSITORY", "Roboatlas21/Trailers4Jellyfin")
ZIP_PREFIX = "trailers4jellyfin"


def main() -> None:
    if len(sys.argv) != 3:
        print(f"Usage: {sys.argv[0]} <version> <checksum>")
        sys.exit(1)

    version = sys.argv[1]
    checksum = sys.argv[2]

    with open(BUILD_YAML) as f:
        build = yaml.safe_load(f)

    if str(build["version"]) != version:
        raise ValueError("Release version does not match build.yaml")

    target_abi = build["targetAbi"]
    changelog = "".join(f"- {item}\n" for item in build.get("changelog", []))
    source_url = f"https://github.com/{REPO}/releases/download/v{version}/{ZIP_PREFIX}_{version}.zip"
    timestamp = datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")

    new_entry = {
        "checksum": checksum,
        "changelog": changelog,
        "targetAbi": target_abi,
        "sourceUrl": source_url,
        "timestamp": timestamp,
        "version": version,
    }

    with open(MANIFEST_FILE) as f:
        manifest = json.load(f)

    for key in ("name", "guid", "category", "description", "overview", "owner"):
        manifest[0][key] = build[key]
    manifest[0]["imageUrl"] = f"https://raw.githubusercontent.com/{REPO}/main/Jellyfin.Plugin.Trailers4Jellyfin/Images/logo.svg"

    existing = next((v for v in manifest[0]["versions"] if v["version"] == version), None)
    if existing is not None:
        if any(existing[key] != new_entry[key] for key in ("checksum", "sourceUrl", "targetAbi")):
            raise ValueError("Published version already exists with different release metadata; bump the version")
        print(f"Version {version} already exists in manifest. Nothing to do.")
        return

    # Newest version first
    manifest[0]["versions"] = [new_entry] + manifest[0]["versions"]

    with open(MANIFEST_FILE, "w") as f:
        json.dump(manifest, f, indent=4)
        f.write("\n")

    print(f"manifest.json updated with version {version}")


if __name__ == "__main__":
    main()

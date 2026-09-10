#!/usr/bin/env python3
"""Insert or replace one version entry in the Jellyfin repository manifest.

The manifest advertises every build of the plugin at once and the server picks
what it can run, so this merges into the existing versions list rather than
rewriting it -- the entry for the Jellyfin 10.11 build has to survive a release
of the 12 line.

Entries are kept sorted newest version first. That is presentation only: the
server reads the whole list and chooses for itself.
"""

from __future__ import annotations

import argparse
import json
import sys


def parse_version(text: str) -> tuple[int, ...]:
    """Parse a version the way .NET's System.Version compares one.

    Components are compared numerically and a missing component is not the same
    as a zero, so pad to four before comparing: "12.0.0" and "12.0.0.0" must
    order as equal here, where .NET would treat the shorter one as lower.
    """
    parts = [int(p) for p in text.split(".")]
    if not 2 <= len(parts) <= 4:
        raise ValueError(f"expected 2 to 4 version components, got {text!r}")
    return tuple(parts + [0] * (4 - len(parts)))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--manifest", required=True)
    ap.add_argument("--guid", required=True)
    ap.add_argument("--version", required=True)
    ap.add_argument("--target-abi", required=True)
    ap.add_argument("--source-url", required=True)
    ap.add_argument("--checksum", required=True)
    ap.add_argument("--changelog", default="")
    ap.add_argument("--timestamp", required=True)
    args = ap.parse_args()

    # Fail loudly on a malformed version rather than writing an entry the server
    # would silently skip.
    parse_version(args.version)
    parse_version(args.target_abi)

    if len(args.checksum) != 32 or not all(c in "0123456789abcdef" for c in args.checksum):
        print(f"error: checksum is not a lowercase MD5 hex digest: {args.checksum!r}", file=sys.stderr)
        return 1

    with open(args.manifest, encoding="utf-8") as fh:
        manifest = json.load(fh)

    if not isinstance(manifest, list):
        print("error: a repository manifest must be a JSON array of plugins", file=sys.stderr)
        return 1

    plugins = [p for p in manifest if p.get("guid", "").lower() == args.guid.lower()]
    if len(plugins) != 1:
        print(f"error: expected exactly one plugin with guid {args.guid}, found {len(plugins)}", file=sys.stderr)
        return 1
    plugin = plugins[0]

    entry = {
        "version": args.version,
        "changelog": args.changelog,
        "targetAbi": args.target_abi,
        "sourceUrl": args.source_url,
        "checksum": args.checksum,
        "timestamp": args.timestamp,
    }

    versions = plugin.get("versions", [])
    replaced = any(v.get("version") == args.version for v in versions)
    # Keep any existing entry's changelog when this run has nothing better to say.
    if replaced and not args.changelog:
        entry["changelog"] = next(
            (v.get("changelog", "") for v in versions if v.get("version") == args.version), ""
        )
    versions = [v for v in versions if v.get("version") != args.version]
    versions.append(entry)
    versions.sort(key=lambda v: parse_version(v["version"]), reverse=True)
    plugin["versions"] = versions

    with open(args.manifest, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(manifest, fh, indent=2, ensure_ascii=False)
        fh.write("\n")

    print(f"{'replaced' if replaced else 'added'} version {args.version} (targetAbi {args.target_abi})")
    print("manifest now advertises: " + ", ".join(v["version"] for v in versions))
    return 0


if __name__ == "__main__":
    sys.exit(main())

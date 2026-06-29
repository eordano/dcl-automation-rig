#!/usr/bin/env python3
"""Consolidate raw URL-walk screenshots into the code-named atlas.

PORTED from the Unity rig's consolidate-atlas.py — the keep-best / subset /
back-up discipline is route-source agnostic, so it ports unchanged in substance.
Only the docstring is retargeted: the captures here come from rig/atlas/url-walk.sh
(chromium navigated to each catalyst-served bevy-overlay route), not a Unity
harness, but the naming contract is identical.

Capture produces PNGs named NNN_<route>.png (NNN = capture order, route = the
route name from routes.json, e.g. 003_settingsgraphics.png). This renames them to
<CODE>-<route>.png using the canonical route->code registry (atlas-codes.json
next to this script).

  - route in the registry      -> <out>/<CODE>-<route>.png   (latest wins = keep-best)
  - hud_* / *_composite / …    -> <out>/_context/<route>.png (no single code)
  - unknown route              -> left in place + reported

Output dir defaults to ./shots/atlas under the repo (override with
DCL_ATLAS_OUT or --out). No absolute paths are baked in.

Usage: consolidate-atlas.py [--out DIR] <source_dir> [<source_dir> ...]
"""
import argparse
import glob
import json
import os
import re
import shutil
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
CODES = json.load(open(os.path.join(ROOT, "atlas-codes.json")))["codes"]


def route_of(fname):
    stem = os.path.basename(fname)
    stem = stem[:-4] if stem.lower().endswith(".png") else stem
    return re.sub(r"^\d+_", "", stem)  # strip the NNN_ capture-order prefix


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.environ.get(
        "DCL_ATLAS_OUT", os.path.join(ROOT, "shots", "atlas")))
    ap.add_argument("src", nargs="+")
    args = ap.parse_args()

    atlas = args.out
    ctx = os.path.join(atlas, "_context")
    os.makedirs(ctx, exist_ok=True)

    named, context, unknown = [], [], []
    for d in args.src:
        for f in sorted(glob.glob(os.path.join(d, "*.png"))):
            route = route_of(f)
            if route in CODES:
                dst = os.path.join(atlas, f"{CODES[route]}-{route}.png")
                shutil.copy2(f, dst)
                named.append(os.path.basename(dst))
            elif (route.startswith("hud_") or "composite" in route
                  or route.startswith("atlas_") or route.startswith("auth_")):
                shutil.copy2(f, os.path.join(ctx, route + ".png"))
                context.append(route)
            else:
                unknown.append(f)

    print(f"named={len(named)} context={len(context)} unknown={len(unknown)} -> {atlas}")
    if named:
        print("  ATLAS:", ", ".join(sorted(set(named))))
    if context:
        print("  CONTEXT:", ", ".join(sorted(set(context))))
    if unknown:
        print("  UNKNOWN (left in source):", ", ".join(unknown))


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit("usage: consolidate-atlas.py [--out DIR] <source_dir> [...]")
    main()

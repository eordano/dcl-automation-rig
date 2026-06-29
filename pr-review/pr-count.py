#!/usr/bin/env python3
# pr-count.py <report.json> — print the number of atlas SCREEN markers in the report
# (excludes the atlas_teleport_quiet infra marker). 0 = incomplete/partial run.
import json, sys
raw = open(sys.argv[1], "rb").read()
for b in (b"\xef\xbb\xbf", b"\xff\xfe", b"\xfe\xff"):
    if raw.startswith(b): raw = raw[len(b):]
t = raw.decode("utf-8", "replace"); i = t.find("{")
try: r = json.loads(t[i:]) if i >= 0 else {}
except Exception: r = {}
print(len([x for x in r.get("actions", [])
           if str(x.get("label","")).startswith("atlas_") and x.get("label") != "atlas_teleport_quiet"]))

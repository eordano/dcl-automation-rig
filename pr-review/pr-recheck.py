#!/usr/bin/env python3
# pr-recheck.py <recapture_report.json> "route1,route2,..." — classify the re-captured suspects.
# Prints three lines:
#   recovered=<routes shown on recapture (flaky first pass)>
#   confirmed=<routes still broken on recapture (real regression)>
#   missing=<suspects with no marker on recapture (inconclusive)>
import json, sys
rep, suspects = sys.argv[1], [s for s in sys.argv[2].split(",") if s]
raw = open(rep, "rb").read()
for bom in (b"\xef\xbb\xbf", b"\xff\xfe", b"\xfe\xff"):
    if raw.startswith(bom): raw = raw[len(bom):]
txt = raw.decode("utf-8", "replace"); i = txt.find("{")
try: r = json.loads(txt[i:]) if i >= 0 else {}
except Exception: r = {}
acts = r.get("actions", []) if isinstance(r, dict) else []
status = {}
for a in acts:
    lab = str(a.get("label",""))
    if lab.startswith("atlas_"):
        status[lab[len("atlas_"):]] = str(a.get("error","")).startswith("shown")
recovered = [s for s in suspects if status.get(s) is True]
confirmed = [s for s in suspects if status.get(s) is False]
missing   = [s for s in suspects if s not in status]
print("recovered=" + ",".join(recovered))
print("confirmed=" + ",".join(confirmed))
print("missing="   + ",".join(missing))

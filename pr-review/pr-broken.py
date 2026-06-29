#!/usr/bin/env python3
# pr-broken.py <report.json> — print comma-separated atlas routes that are broken
# BEYOND the baseline. Baseline = our-stack-on-fresh-dev broken set (pr-review-baseline.txt),
# overridable via env PR_REVIEW_BASELINE (empty string = no baseline -> report ALL broken,
# used to GENERATE the baseline). Empty output = nothing suspicious.
import json, os, sys
def load_baseline():
    env = os.environ.get("PR_REVIEW_BASELINE")
    if env is not None:
        return set(x.strip() for x in env.split(",") if x.strip())
    p = os.path.join(os.path.dirname(os.path.abspath(__file__)), "pr-review-baseline.txt")
    if os.path.exists(p):
        # web-stack: the baseline file is comment-documented; skip '#' lines so a
        # comment never becomes a phantom baseline route. (Empty set = no known
        # pre-broken route — the GATED surfaces are unwired, not "broken".)
        lines = [ln.split("#", 1)[0] for ln in open(p)]
        return set(x.strip() for x in ",".join(lines).split(",") if x.strip())
    return set()
BASELINE = load_baseline()
INFRA = {"atlas_teleport_quiet"}
raw = open(sys.argv[1], "rb").read()
for bom in (b"\xef\xbb\xbf", b"\xff\xfe", b"\xfe\xff"):
    if raw.startswith(bom): raw = raw[len(bom):]
txt = raw.decode("utf-8", "replace"); i = txt.find("{")
try: r = json.loads(txt[i:]) if i >= 0 else {}
except Exception: r = {}
acts = r.get("actions", []) if isinstance(r, dict) else []
broke = sorted(a["label"][len("atlas_"):] for a in acts
               if str(a.get("label","")).startswith("atlas_") and a.get("label") not in INFRA
               and not str(a.get("error","")).startswith("shown"))
print(",".join(x for x in broke if x not in BASELINE))

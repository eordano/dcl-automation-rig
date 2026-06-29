#!/usr/bin/env python3
# pr-write-review.py — write a PR review markdown from a harness run.
# PORTED from the Unity rig (PASS/REGRESSION/CONFLICT/COMPILE-FAIL verdict
# rendering, screen-health, re-verify folding, in-depth-code-review fold). The
# verdict state machine is unchanged; the wording is retargeted at the web-stack
# compile (cargo + tsc + cargo) and capture (atlas URL-walk + web product tour)
# instead of the Unity 44-commit-stack + batchmode-compile + VM-atlas.
#
# Inputs via env: PR_NUM PR_TITLE PR_HEAD STACK_STATUS STACK_FILES COMPILE_STATUS
#   COMPILE_ERRORS (path, optional) SHOT_DIR REVIEW_PATH STACK_TIP UTC
# Optional argv[1] = harness-report.json (the atlas/session run). If absent/empty,
# the run terminated early (conflict or compile-fail) and only those sections are written.
# Baseline broken-set is data-driven via pr-review-baseline.txt / PR_REVIEW_BASELINE.
# Prints a one-line summary for the loop log.
import json, os, sys, glob, re
from collections import Counter

E = os.environ.get
PR_NUM   = E("PR_NUM", "?")
TITLE    = E("PR_TITLE", "")
HEAD     = E("PR_HEAD", "?")
STACK    = E("STACK_STATUS", "?")      # clean | conflict | rebase-conflict | error
STACK_FILES = E("STACK_FILES", "")
STACK_TIP = E("STACK_TIP", "")
COMPILE  = E("COMPILE_STATUS", "")     # ok | fail | skipped
SHOT_DIR = E("SHOT_DIR", "")
REVIEW   = E("REVIEW_PATH", "/dev/stdout")
UTC      = E("UTC", "")
FIX_BRANCH = E("DCL_REVIEW_FIX_BRANCH", "auto-fixes")

def _load_baseline():
    env = os.environ.get("PR_REVIEW_BASELINE")
    if env is not None:
        return set(x.strip() for x in env.split(",") if x.strip())
    p = os.path.join(os.path.dirname(os.path.abspath(__file__)), "pr-review-baseline.txt")
    if os.path.exists(p):
        lines = [ln.split("#", 1)[0] for ln in open(p)]   # skip '#' comments
        return set(x.strip() for x in ",".join(lines).split(",") if x.strip())
    return set()                       # web-stack: no known-broken baseline route by default
BASELINE_BROKEN = _load_baseline()

report = None
rp = sys.argv[1] if len(sys.argv) > 1 else ""
if rp and os.path.exists(rp) and os.path.getsize(rp) > 0:
    raw = open(rp, "rb").read()
    for bom in (b"\xef\xbb\xbf", b"\xff\xfe", b"\xfe\xff"):
        if raw.startswith(bom): raw = raw[len(bom):]
    txt = raw.decode("utf-8", "replace")
    i = txt.find("{")
    try: report = json.loads(txt[i:]) if i >= 0 else None
    except Exception: report = None

def screens(r):
    # The capture report marks each captured screen as an action labelled
    # atlas_<route>; error=="shown..." means it rendered. (Same contract the
    # consolidate/url-walk path emits; pr-recheck.py reads it identically.)
    acts = r.get("actions", []) if isinstance(r, dict) else []
    INFRA = {"atlas_teleport_quiet"}
    s = [a for a in acts if str(a.get("label","")).startswith("atlas_") and a.get("label") not in INFRA]
    work = [a["label"][len("atlas_"):] for a in s if str(a.get("error","")).startswith("shown")]
    broke = [a["label"][len("atlas_"):] for a in s if not str(a.get("error","")).startswith("shown")]
    return sorted(work), sorted(broke)

L = []
L.append(f"# PR #{PR_NUM} — review (our `{FIX_BRANCH}` stack on the web client)")
L.append("")
L.append(f"- **title:** {TITLE}")
L.append(f"- **pr head sha:** {HEAD}")
L.append(f"- **stack tip sha:** {STACK_TIP or '(n/a)'}")
L.append(f"- **utc:** {UTC}")
L.append(f"- **base:** our `{FIX_BRANCH}` fixes cherry-picked onto the PR head")
L.append("")

verdict = "PASS"; summary = ""

# --- Rebase onto base + stack ---
SKIPPED = E("STACK_SKIPPED", "")
L.append("## 1. Rebase onto fresh base + stack our fixes")
if STACK == "rebase-conflict":
    verdict = "REBASE-CONFLICT"
    L.append("🔴 **PR does not rebase cleanly onto the current base** — it's stale and needs a rebase before merge. Conflicting files:")
    for f in [x for x in STACK_FILES.split(",") if x]:
        L.append(f"  - `{f}`")
    L.append("")
    L.append("Capture skipped (can't build a non-rebasing branch); the in-depth code review below still applies to the PR's diff.")
elif STACK == "clean":
    L.append("✅ PR rebases cleanly onto the base, and our fixes stack on top.")
    if SKIPPED and SKIPPED != "0":
        L.append(f"- ℹ️ {SKIPPED} of our fix commits are **already in this PR** (empty cherry-pick → skipped) — the PR overlaps our work.")
elif STACK == "conflict":
    verdict = "CONFLICT"
    L.append("🔴 **Conflict stacking our fixes** onto the rebased PR. Conflicting files:")
    for f in [x for x in STACK_FILES.split(",") if x]:
        L.append(f"  - `{f}`")
    L.append("")
    L.append("This PR touches the same code as one or more of our fixes; manual reconciliation needed.")
else:
    verdict = "ERROR"
    L.append(f"⚠️ Stack step error: {STACK_FILES or STACK}")
L.append("")

# --- Compile (cargo check/build bevy + tsc/build sites + cargo check catalyst) ---
if STACK == "clean":
    L.append("## 2. Compile (cargo bevy + tsc sites + cargo catalyst)")
    if COMPILE == "ok":
        L.append("✅ Compiles clean — `cargo check`/`build` (bevy-explorer) + `tsc`/build (sites/app) + `cargo check` (catalyst) all green.")
    elif COMPILE == "fail":
        verdict = "COMPILE-FAIL"
        L.append("🔴 **Compile errors** with our stack applied:")
        ce = E("COMPILE_ERRORS","")
        if ce and os.path.exists(ce):
            for ln in [l.strip() for l in open(ce, encoding="utf-8", errors="replace")][:40]:
                if re.search(r"error(\[|:)|error TS", ln): L.append(f"  - `{ln[:160]}`")
        L.append("")
        L.append("PR + our stack does not build — likely the PR changed an API our commits depend on (or vice-versa).")
    else:
        L.append("_skipped_")
    L.append("")

# --- Capture / runtime (atlas URL-walk + web product tour) ---
if report is not None and not screens(report)[0] and not screens(report)[1]:
    verdict = "RUN-INCOMPLETE" if verdict == "PASS" else verdict
    L.append("## 3. Capture + screen health (atlas URL-walk + web tour)")
    L.append("⚠️ Capture produced **no screen markers** — the run was incomplete (stall / partial report). "
             "This is an environment hiccup, not a PR signal; the loop will re-review this PR on a later tick.")
    L.append("")
    report = None

if report is not None:
    work, broke = screens(report)
    suspects = sorted(set(broke) - BASELINE_BROKEN)           # broken beyond baseline (first pass)
    baseline_recovered = sorted(BASELINE_BROKEN - set(broke))
    reverified = E("REVERIFIED") == "1"
    confirmed = [x for x in E("CONFIRMED_REGRESS","").split(",") if x]
    flaky     = [x for x in E("FLAKY_RECOVERED","").split(",") if x]
    inconc    = [x for x in E("INCONCLUSIVE","").split(",") if x]
    ri = report.get("reachedInteractive"); tti = report.get("timeToInteractiveSeconds"); ec = report.get("errorCount")
    L.append("## 3. Capture + screen health (atlas URL-walk + web tour)")
    if ri is not None or tti is not None or ec is not None:
        L.append(f"- reachedInteractive: **{ri}**  ·  tti: {round(tti,1) if isinstance(tti,(int,float)) else tti}s  ·  errorCount: {ec}")
    L.append(f"- screens: **{len(work)} working / {len(broke)} broken** (of {len(work)+len(broke)} captured)")
    if broke:
        L.append(f"- broken (first pass): {', '.join(broke)}")
    if reverified:
        L.append(f"- _re-verified {len(suspects)} broken-beyond-baseline screen(s) via subset recapture_")
        if confirmed:
            verdict = "REGRESSION"
            L.append(f"- 🔴 **REGRESSION confirmed** (still broken after recapture): **{', '.join(confirmed)}**")
        else:
            L.append("- ✅ no confirmed regression (suspects did not reproduce on recapture)")
        if flaky:
            L.append(f"- 🟡 flaky first pass, passed on recapture (NOT a regression): {', '.join(flaky)}")
        if inconc:
            if verdict == "PASS": verdict = "INCONCLUSIVE"
            L.append(f"- ⚠️ could not re-verify (recapture incomplete): {', '.join(inconc)}")
    else:
        L.append(f"- ✅ no screen regression vs baseline (broken ⊆ baseline: {', '.join(sorted(BASELINE_BROKEN)) or 'none'})")
    if baseline_recovered:
        L.append(f"- 🟢 baseline-broken now working: {', '.join(baseline_recovered)}")
    L.append("- _note: WebGPU canvas is non-present here (functional-only); identity/streams GATED on the worker-gap — see docs/bridge-status.md._")
    L.append("")
    # screenshot inventory
    shots = sorted(os.path.basename(p) for p in glob.glob(os.path.join(SHOT_DIR, "*.png"))) if SHOT_DIR else []
    L.append("## 4. Screenshots")
    L.append(f"{len(shots)} code-named screenshots saved in `{SHOT_DIR}/`")
    if shots:
        L.append("")
        L.append(", ".join(s[:-4] for s in shots))
    L.append("")
elif STACK == "clean" and COMPILE == "ok":
    verdict = "CAPTURE-FAIL" if verdict == "PASS" else verdict
    L.append("## 3. Capture + screen health (atlas URL-walk + web tour)")
    L.append("⚠️ Capture did not produce a report (stall/timeout) — compile was clean but the capture session did not complete. Retry next tick.")
    L.append("")

# --- In-depth code review (rubric, from headless claude) ---
crf = E("CODEREVIEW_FILE", "")
cr_result = ""
if crf and os.path.exists(crf):
    cr = open(crf, encoding="utf-8", errors="replace").read().strip()
    L.append("## In-depth code review (per the project rubric)")
    L.append("")
    L.append(cr if cr else "_(no code review output)_")
    L.append("")
    for ln in cr.splitlines():
        s = ln.strip()
        if s.upper().startswith("REVIEW_RESULT:"):
            cr_result = s.split(":", 1)[1].strip(); break

L.insert(2, f"> **Build/test verdict: {verdict}**" + (f"  ·  **code review: {cr_result}**" if cr_result else ""))
L.insert(3, "")

os.makedirs(os.path.dirname(REVIEW), exist_ok=True) if os.path.dirname(REVIEW) else None
with open(REVIEW, "w", encoding="utf-8") as f:
    f.write("\n".join(L) + "\n")

# one-line summary for the loop log
cr_tag = f" | review:{cr_result}" if cr_result else ""
if report is not None:
    w, b = screens(report)
    reg = [x for x in E("CONFIRMED_REGRESS","").split(",") if x] if E("REVERIFIED")=="1" else sorted(set(b)-BASELINE_BROKEN)
    summary = f"PR #{PR_NUM}: {verdict} — {len(w)}/{len(w)+len(b)} screens" + (f", REGRESS:{','.join(reg)}" if reg else "") + cr_tag
else:
    summary = f"PR #{PR_NUM}: {verdict}" + (f" ({STACK_FILES})" if STACK in ('conflict','rebase-conflict') else "") + cr_tag
print(summary)

#!/usr/bin/env bash
# =============================================================================
# atlas-runner.sh — ONE remote-trigger tick for the Mac atlas tour. Target: "mac".
#
# This is the OUTER poll that wraps mac/atlas-capture.sh (the no-relaunch IPC
# tour). It is the Mac analogue of loop/run-loop.sh: each invocation is one
# bounded, idempotent tick —
#
#   lock → check trigger → (if fired) run tour in-session → publish gallery
#        → record state → run-log → retention → release lock
#
# A LaunchAgent (mac/com.dcl.atlas-runner.plist) invokes this on an interval, so
# the interval IS the poll cadence. The tour MUST run in the logged-in GUI (Aqua)
# session — only that context can present Metal — which is exactly where a user
# LaunchAgent runs, so the runner inherits a window server. The PR loop (or any
# remote) fires a capture WITHOUT pushing: `touch $DCL_RUNNER_TRIGGER_FILE`, or
# move a watched local branch tip (DCL_RUNNER_TRIGGER_BRANCH). LOCAL ONLY.
#
# Reversible: it edits no source, never commits/pushes, and the LaunchAgent can be
# unloaded at any time. Nothing here is auto-destructive (it only writes under
# $HOME/.dcl-rig and consumes the one-shot trigger file).
#
#   ./mac/atlas-runner.sh            # one tick (what the LaunchAgent calls)
#   touch ~/.dcl-rig/atlas-trigger   # fire a one-shot capture on the next tick
# =============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/../config.sh" 2>/dev/null || true
. "$HERE/../lib/common.sh"

[ "$(uname -s)" = Darwin ] || dcl_die "mac/atlas-runner.sh is macOS-only (uname=$(uname -s))"

LOGS="$DCL_RUNNER_LOGS"; mkdir -p "$LOGS"
TS="$(date -u +%Y%m%dT%H%M%SZ)"
RUNLOG="$LOGS/run-$TS.md"
note() { printf '%s\n' "$*" >> "$RUNLOG"; }

# --- 1. Concurrency lock (mirror loop/run-loop.sh: TTL + stale takeover) ------
LOCK="$DCL_RUNNER_LOCK"; mkdir -p "$(dirname "$LOCK")"
if [ -f "$LOCK" ]; then
    age=$(( $(date +%s) - $(stat -c %Y "$LOCK" 2>/dev/null || stat -f %m "$LOCK" 2>/dev/null || echo 0) ))
    if [ "$age" -lt "$DCL_RUNNER_LOCK_TTL" ]; then
        # Busy (a tour is still running, or another tick). Skip silently — no log spam.
        exit 0
    fi
    dcl_log "stale runner lock (${age}s) — taking over"
fi
date +%s > "$LOCK"
trap 'rm -f "$LOCK"' EXIT          # ALWAYS release, even on error

# --- 2. Trigger detection (either/both sources; neither => idle, no work) -----
# FILE: one-shot — present means "run once"; consumed (removed) after handling so
#       it never re-fires. BRANCH: tip sha differs from the last handled sha.
fired=0; reason=""
if [ -n "${DCL_RUNNER_TRIGGER_FILE:-}" ] && [ -e "$DCL_RUNNER_TRIGGER_FILE" ]; then
    fired=1; reason="file"
fi
branch_tip=""
if [ -n "${DCL_RUNNER_TRIGGER_BRANCH:-}" ]; then
    branch_tip="$(git -C "$DCL_RUNNER_TRIGGER_REPO" rev-parse "refs/heads/$DCL_RUNNER_TRIGGER_BRANCH" 2>/dev/null || true)"
    last_tip="$(sed -n 's/^branch=//p' "$DCL_RUNNER_STATE" 2>/dev/null | tail -1)"
    if [ -n "$branch_tip" ] && [ "$branch_tip" != "$last_tip" ]; then
        fired=1; reason="${reason:+$reason,}branch:${DCL_RUNNER_TRIGGER_BRANCH}@${branch_tip:0:8}"
    fi
fi
if [ "$fired" != 1 ]; then exit 0; fi   # nothing to do — quiet idle tick

note "# atlas-runner tick — $TS"
note "## trigger: $reason"
dcl_log "atlas-runner: trigger fired ($reason) — running $DCL_RUNNER_MODE tour"

# --- 3. Consume the trigger FIRST (one-shot semantics; avoids re-fire loops) --
# Record the branch sha now (success or fail) so a broken tip doesn't re-fire
# every tick; remove the one-shot file so it runs exactly once per touch.
mkdir -p "$(dirname "$DCL_RUNNER_STATE")"
if [ -n "$branch_tip" ]; then printf 'branch=%s\n' "$branch_tip" > "$DCL_RUNNER_STATE"; fi
[ -e "${DCL_RUNNER_TRIGGER_FILE:-}" ] && rm -f "$DCL_RUNNER_TRIGGER_FILE"

# --- 4. Run the tour (no-relaunch IPC flow; launches/reuses the in-session editor)
# atlas-capture.sh sets capture paths + subset over IPC and consolidates to
# $DCL_ATLAS_OUT (default $DCL_MAC_SHOTS/atlas). It is self-contained; we just run it.
note "## tour — mac/atlas-capture.sh $DCL_RUNNER_MODE"
tour_rc=0
"$HERE/atlas-capture.sh" "$DCL_RUNNER_MODE" >>"$RUNLOG" 2>&1 || tour_rc=$?
ATLAS_OUT="${DCL_ATLAS_OUT:-$DCL_MAC_SHOTS/atlas}"
shots="$(find "$ATLAS_OUT" -maxdepth 1 -name '*.png' 2>/dev/null | wc -l | tr -d ' ')"
note "## tour rc=$tour_rc, consolidated shots=$shots in $ATLAS_OUT"

# --- 5. Publish the consolidated gallery (timestamped + 'latest') ------------
# rsync if available (handles local dir OR user@host:path OR remote); else cp -R
# for a local destination. Non-fatal: a publish failure still leaves the local
# gallery + run-log intact.
if [ "${shots:-0}" -gt 0 ]; then
    dest="$DCL_RUNNER_PUBLISH"
    case "$dest" in *:*) remote=1 ;; *) remote=0; mkdir -p "$dest" ;; esac
    if command -v rsync >/dev/null 2>&1; then
        rsync -a --delete "$ATLAS_OUT/" "$dest/$TS/" >>"$RUNLOG" 2>&1 \
            && note "## published -> $dest/$TS/ (rsync)" || note "## publish FAILED (rsync; see above)"
        # 'latest' convenience mirror (local dest only; remotes get the timestamped dir)
        [ "$remote" = 0 ] && rsync -a --delete "$ATLAS_OUT/" "$dest/latest/" >>"$RUNLOG" 2>&1 || true
    elif [ "$remote" = 0 ]; then
        mkdir -p "$dest/$TS" && cp -R "$ATLAS_OUT/." "$dest/$TS/" \
            && note "## published -> $dest/$TS/ (cp)" || note "## publish FAILED (cp)"
        rm -rf "$dest/latest" 2>/dev/null; mkdir -p "$dest/latest" && cp -R "$ATLAS_OUT/." "$dest/latest/" 2>/dev/null || true
    else
        note "## publish SKIPPED — remote dest '$dest' needs rsync (not found)"
    fi
    # also keep the machine-readable report alongside the gallery
    [ -f "${DCL_HARNESS_REPORT:-}" ] && [ "$remote" = 0 ] && cp -f "$DCL_HARNESS_REPORT" "$dest/$TS/harness-report.json" 2>/dev/null || true
else
    note "## nothing to publish (0 shots) — tour likely failed; see run-log"
fi

# --- 6. Retention ------------------------------------------------------------
ls -t "$LOGS"/run-*.md 2>/dev/null | tail -n +"$((DCL_RUNNER_KEEP+1))" | xargs -r rm -f
case "$DCL_RUNNER_PUBLISH" in
    *:*) : ;;  # remote: don't prune
    *) ls -dt "$DCL_RUNNER_PUBLISH"/*/ 2>/dev/null | grep -v '/latest/$' \
         | tail -n +"$((DCL_RUNNER_KEEP+1))" | xargs -r rm -rf ;;
esac

note "## tick complete (rc=$tour_rc, shots=$shots)"
dcl_log "atlas-runner tick $TS done -> $RUNLOG (rc=$tour_rc, shots=$shots)"
exit 0

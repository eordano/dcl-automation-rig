#!/usr/bin/env bash
# =============================================================================
# atlas-capture.sh — drive the harness UI-capture modes on Mac via ClaudeIPC,
# writing the force-shown UI/auth screens to a Mac path, then consolidate to
# <CODE>-<route>.png with the vendored registry. Target: "mac".
#
#   ./mac/atlas-capture.sh auth     # pre-world auth/OTP/web3/lobby screens
#   ./mac/atlas-capture.sh atlas    # in-world UI surfaces (quiet parcel 140,140)
#
# The atlas drivers (AtlasCapture_<route>) live inside the vendored harness
# master (unity/DclPlaytestHarness.cs). Its output paths were Windows-only; the
# Mac-parity edit made them env-overridable (DCL_HARNESS_SHOTS/REPORT), which we
# set here. Because the harness reads them at editor launch, we relaunch so they
# apply. The native CaptureShot path composites screen-space UI correctly (unlike
# screenshot-game), which is why UI capture goes through the harness, not IPC.
#
# Status: the IPC wiring + path override are verified on this host; the per-screen
# drivers reflect against the live MVC graph and are adapted from the VM harness —
# treat individual screens as needing per-run verification (see docs/12 tiers).
# =============================================================================
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/../config.sh"
. "$HERE/../lib/common.sh"
. "$HERE/../lib/mac-driver.sh"
. "$HERE/../lib/mac-ipc.sh"

[ "$(uname -s)" = Darwin ] || dcl_die "mac/atlas-capture.sh is macOS-only"
MODE="${1:-atlas}"
case "$MODE" in
    auth)  METHOD=DCL.Harness.DclPlaytestHarness.RunAuthCaptureFromMenu ;;
    atlas) METHOD=DCL.Harness.DclPlaytestHarness.RunAtlasFromMenu ;;
    *) dcl_die "mode must be 'auth' or 'atlas'" ;;
esac

# Point the harness output at Mac paths (read at editor launch — see header).
export DCL_HARNESS_SHOTS="${DCL_HARNESS_SHOTS:-$DCL_MAC_SHOTS/harness-shots}"
export DCL_HARNESS_REPORT="${DCL_HARNESS_REPORT:-$DCL_MAC_SHOTS/harness-report.json}"
mkdir -p "$DCL_HARNESS_SHOTS"
rm -f "$DCL_HARNESS_REPORT"

# Ensure the editor is up — idempotent: reuses a running one, else launches fresh WITH our
# env exported (covers the cold-start case). We deliberately do NOT kill+relaunch a running
# editor to inject DCL_HARNESS_*: on macOS, quitting/killing the editor makes Unity HUB
# re-exec a REPLACEMENT editor WITHOUT our environment (so shots/report fell back to the
# Windows C:\ path and nothing landed — observed). Instead we set the output paths (and any
# subset) at RUNTIME over IPC below, which works regardless of how the editor was launched.
"$HERE/editor-up.sh"

# Wait for ClaudeIPC to actually arm. editor-up.sh returns when the editor WINDOW
# is up, but ClaudeIPC's [InitializeOnLoadMethod] only runs after the on-boot
# script compile finishes its domain reload, and the heartbeat lands a tick later
# — that's several SECONDS past "window up" on a cold boot. The old fixed 8s grace
# raced it and spuriously died ("not alive after relaunch") while the watcher was
# arming. Poll up to DCL_IPC_WAIT instead.
ipc_wait="${DCL_IPC_WAIT:-120}"; ipc_ok=0
for _ in $(seq 1 "$ipc_wait"); do
    if dcl_ipc_alive 2>/dev/null; then ipc_ok=1; break; fi
    sleep 1
done
[ "$ipc_ok" = 1 ] || dcl_die "ClaudeIPC not alive after ${ipc_wait}s (compile failed, or watcher never armed — see $DCL_MAC_LOG)"

# Set the harness output paths on the LIVE editor over IPC (replaces the fragile relaunch). This
# applies whether the editor is one WE launched (has the env too) or a pre-existing / Hub-spawned
# one (no env) — so the report + shots always land at the Mac paths the wait/consolidate below read.
dcl_log "set capture paths via IPC: shots=$DCL_HARNESS_SHOTS report=$DCL_HARNESS_REPORT"
dcl_ipc exec method=DCL.Harness.DclPlaytestHarness.SetCapturePaths \
    arg.shots="$DCL_HARNESS_SHOTS" arg.report="$DCL_HARNESS_REPORT" __timeout=15 | grep -q '"ok":true' \
    || dcl_die "SetCapturePaths failed (harness not compiled? see $DCL_MAC_LOG)"

# Subset (fast per-route re-verification): set it at runtime too, so it works on a reused editor
# whose launch-time DCL_ATLAS_ONLY env we can't change. Empty clears (full run).
dcl_ipc exec method=DCL.Harness.DclPlaytestHarness.SetAtlasOnly \
    arg.csv="${DCL_ATLAS_ONLY:-}" __timeout=15 >/dev/null 2>&1 || true

dcl_log "exec $METHOD — arms $MODE capture (enters Play, force-shows each screen)"
dcl_ipc exec method="$METHOD" __timeout=20 | grep -q '"ok":true' || dcl_die "exec $METHOD failed"

# Capture the editor pid so the wait loop can detect an actual CRASH (process
# death) immediately, instead of idling until the plateau. Unity can hard-crash
# on a content/Localization fault mid-capture; without this we'd waste the full
# plateau window — or, if shots were still flushing, never trip the plateau.
edpid="$(dcl_mac_editor_pid || true)"
dcl_log "watching editor pid ${edpid:-?} for crash"

# Wait for the report (written when the driver list finishes) OR a shot PLATEAU
# (harness stalled — e.g. the auth Localization bug) OR the editor CRASHING.
# Any of the three stops the wait and consolidates whatever was captured, so a
# crash/stall never hangs the run or silently loses the shots.
dcl_log "waiting for harness report (or plateau, or crash), up to ${DCL_ATLAS_TIMEOUT:-300}s…"
plateau="${DCL_ATLAS_PLATEAU:-60}"; last=-1; still=0; crashed=0
for _ in $(seq 1 "${DCL_ATLAS_TIMEOUT:-300}"); do
    [ -f "$DCL_HARNESS_REPORT" ] && { dcl_log "report written"; break; }
    # CRASH CHECK: editor process gone (and report not yet written) = it died.
    if [ -n "$edpid" ] && ! kill -0 "$edpid" 2>/dev/null; then
        crashed=1
        dcl_log "EDITOR CRASHED (pid $edpid gone) — consolidating what was captured"; break
    fi
    # find (not an ls glob) so an EMPTY dir right after the harness reset doesn't
    # fail the glob → exit 2 → set -e kill before the first shot lands.
    n="$(find "$DCL_HARNESS_SHOTS" -maxdepth 1 -name '*.png' 2>/dev/null | wc -l | tr -d ' ')"
    if [ "$n" = "$last" ]; then still=$((still + 1)); else still=0; last="$n"; fi
    if [ "${n:-0}" -gt 0 ] && [ "$still" -ge "$plateau" ]; then
        dcl_log "no new shots for ${plateau}s (stalled or finished w/o report) — consolidating $n"; break
    fi
    sleep 1
done

n="$(find "$DCL_HARNESS_SHOTS" -maxdepth 1 -name '*.png' 2>/dev/null | wc -l | tr -d ' ')"
dcl_log "raw shots captured: $n in $DCL_HARNESS_SHOTS"
ls -1 "$DCL_HARNESS_SHOTS" 2>/dev/null | head

# Consolidate raw NNN_<label>.png → <CODE>-<route>.png using the vendored registry.
OUT="${DCL_ATLAS_OUT:-$DCL_MAC_SHOTS/atlas}"; mkdir -p "$OUT"
if [ "$n" -gt 0 ] && [ -f "$DCL_RIG_REPO/atlas/consolidate-atlas.py" ]; then
    python3 "$DCL_RIG_REPO/atlas/consolidate-atlas.py" --out "$OUT" "$DCL_HARNESS_SHOTS" 2>&1 | tail -8 || true
    echo "consolidated → $OUT"
fi

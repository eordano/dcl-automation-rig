#!/usr/bin/env bash
# =============================================================================
# run-loop.sh — one tick of the autonomous QA loop against the bevy-on-react
# stack. The web-stack port of the Unity rig's run-loop.sh.
#
# The SCAFFOLDING ports intact: lock+TTL, read-state (honor pause), preflight,
# run-session, regression-sentinel gate, retention, never edit/commit here. Each
# invocation is one bounded, idempotent tick:
#
#   lock → read state (honor pause) → preflight → session → regression check
#        → write run-log → retention → release lock
#
# What SWAPPED: the inner session. The Unity rig ran vm/run-playtest.sh (Unity in
# a Windows VM); this runs loop/session.sh — a bevy-on-react session (web product
# tour + React-HUD URL-walk + a catalyst health check). Everything mechanical is
# identical; only the thing being driven changed.
#
# It deliberately does NOT edit code or commit (choosing/applying fixes is the
# operator/agent's judgement — see README + docs/00 autonomous-loop guardrails:
# PR-sized commits, verified-only, never `git add -A`, NEVER push).
#
# HONEST NOTE: the production loop is paused/unverified even for Unity. This is
# the scaffolding + a swapped session, not a proven turnkey loop.
# =============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/../config.sh" 2>/dev/null || true
. "$HERE/../lib/common.sh"

STATE="${DCL_LOOP_STATE:-$HERE/dcl-loop-state.md}"
LOGS="${DCL_LOOP_LOGS:-$HERE/logs}"; mkdir -p "$LOGS"
LOCK="${DCL_LOOP_LOCK:-$HERE/.dcl-loop.lock}"
LOCK_TTL="${DCL_LOOP_LOCK_TTL:-5400}"          # 90 min: a stuck tick's lock auto-expires
CHECKS="${DCL_LOOP_CHECKS:-$HERE/regression-checks.txt}"
TS="$(date -u +%Y%m%dT%H%M%SZ)"
RUNLOG="$LOGS/run-$TS.md"

note() { printf '%s\n' "$*" >> "$RUNLOG"; }

# --- 1. Concurrency lock -----------------------------------------------------
if [ -f "$LOCK" ]; then
    age=$(( $(date +%s) - $(stat -c %Y "$LOCK" 2>/dev/null || echo 0) ))
    if [ "$age" -lt "$LOCK_TTL" ]; then
        echo "# tick $TS — skipped (busy, lock age ${age}s)" > "$LOGS/run-$TS-skip.md"
        exit 0
    fi
    dcl_log "stale lock (${age}s) — taking over"
fi
date +%s > "$LOCK"
trap 'rm -f "$LOCK"' EXIT          # ALWAYS release, even on error

note "# web-stack loop tick — $TS"

# --- 2. Pause banner ---------------------------------------------------------
if [ -f "$STATE" ] && grep -q 'LOOP PAUSED' "$STATE"; then
    note "## paused (user) — banner present in $STATE; no session run."
    exit 0
fi

# --- 3. Preflight ------------------------------------------------------------
# Swapped from "is the VM up?" to "is the bevy-on-react stack reachable?": the
# catalyst-served HUD origin must answer, and (for the web tour) the rig sway
# must be up. We never auto-start them — a tick runs only on a ready stack.
if ! curl -sf -o /dev/null --max-time 10 "${DCL_HUD_BASE:-http://127.0.0.1:3000}"; then
    note "## preflight FAIL — HUD origin ${DCL_HUD_BASE:-http://127.0.0.1:3000} unreachable; not starting it; exiting."
    exit 0
fi
note "## preflight OK — HUD origin reachable."

# --- 4. Session (the swapped bevy-on-react session) --------------------------
note "## session — loop/session.sh"
REPORT="$LOGS/session-$TS.md"
if "$HERE/session.sh" "$REPORT" >>"$RUNLOG" 2>&1; then
    note "## session VALID — report: $REPORT"
else
    note "## session INVALID — environmental (HUD/tour/catalyst). Will retry next tick."
    exit 0
fi

# --- 5. Regression checks ----------------------------------------------------
# Every sentinel must hit 0 in the session report. A hit means a fix regressed
# (or a new class appeared) — fail loudly so the operator investigates.
fails=0
note "## regression checks"
while IFS= read -r pat; do
    pat="${pat%%#*}"; pat="$(echo "$pat" | sed 's/[[:space:]]*$//')"
    [ -z "$pat" ] && continue
    n=$(grep -Ec "$pat" "$REPORT" 2>/dev/null) || true; n=${n:-0}
    if [ "$n" -ne 0 ]; then note "  FAIL ($n)  $pat"; fails=$((fails+1)); else note "  ok        $pat"; fi
done < "$CHECKS"
note "## verdict: $([ "$fails" -eq 0 ] && echo 'ALL PASS (0)' || echo "$fails CHECK(S) FAILED — investigate")"

# --- 6. Retention ------------------------------------------------------------
ls -t "$LOGS"/run-*.md 2>/dev/null | tail -n +"${DCL_LOOP_KEEP:-30}" | xargs -r rm -f
ls -t "$LOGS"/session-*.md 2>/dev/null | tail -n +"${DCL_LOOP_KEEP:-30}" | xargs -r rm -f

note "## tick complete. Update $STATE with the outcome (see template)."
dcl_log "tick $TS done -> $RUNLOG (regression fails=$fails)"
exit 0

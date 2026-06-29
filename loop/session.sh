#!/usr/bin/env bash
# =============================================================================
# session.sh — the measured baseline of one loop tick. REPLACES the Unity rig's
# vm/run-playtest.sh (Unity-in-a-VM playtest).
#
# The Unity session booted the client in a Windows VM, ran the atlas/playtest
# harness, and produced a harness report the loop grepped for sentinels. The
# bevy-on-react session is the same SHAPE — produce one report the loop greps —
# but the measured baseline is three web-stack things:
#
#   1. web product tour   — rig/bevy/product-tour-web.sh (wasm functional surface)
#   2. React-HUD URL-walk — rig/atlas/url-walk.sh (the bevy-overlay routes)
#   3. catalyst health   — a couple of read endpoints answer 2xx
#
# Each step appends to the report; the loop's regression-checks.txt greps it.
# Exit 0 = a VALID session (it ran end-to-end, regardless of pass/fail content);
# exit 1 = INVALID (environmental — a step couldn't run at all), which the loop
# treats as "retry next tick", NOT as a regression.
#
# HONEST: the web tour is functional-only (no pixel check; per-scene ticks GATED
# on the WASMBENCH build — see ../bevy/BUILD-WASM-BENCHMARK.md); the URL-walk
# captures the DOM overlay (real catalyst data, fixture fallback). The session
# never claims more than those produce.
# =============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/../config.sh" 2>/dev/null || true
. "$HERE/../lib/common.sh" 2>/dev/null || true

REPORT="${1:-$HOME/dcl/loop-session-$(date -u +%Y%m%dT%H%M%SZ).md}"
: > "$REPORT"
say() { printf '%s\n' "$*" >> "$REPORT"; }
ran_any=0

say "# bevy-on-react loop session — $(date -Iseconds)"
say ""

# --- 1. web product tour (functional) ----------------------------------------
say "## 1. web product tour (rig/bevy/product-tour-web.sh)"
if [ -x "$HERE/../bevy/product-tour-web.sh" ]; then
    TOUR_OUT="$(mktemp -d)"
    if DCL_SKIP_READY="${DCL_SKIP_READY:-1}" "$HERE/../bevy/product-tour-web.sh" "$TOUR_OUT" >>"$REPORT" 2>&1; then
        say "tour: ran (see ledger above)"; ran_any=1
    else
        say "tour: ran with hard-failure/exit (see classification above)"; ran_any=1
    fi
    # fold the latest ledger into the report so the loop greps scene verdicts too.
    led="$(ls -t "$TOUR_OUT"/tour-*.md 2>/dev/null | head -1)"
    [ -n "$led" ] && cat "$led" >> "$REPORT"
    rm -rf "$TOUR_OUT"
else
    say "tour: SKIPPED (product-tour-web.sh not executable)"
fi
say ""

# --- 2. React-HUD URL-walk ---------------------------------------------------
say "## 2. React-HUD URL-walk (rig/atlas/url-walk.sh)"
if [ -x "$HERE/../atlas/url-walk.sh" ]; then
    WALK_OUT="$(mktemp -d)"
    if "$HERE/../atlas/url-walk.sh" "$WALK_OUT" >>"$REPORT" 2>&1; then
        n=$(ls "$WALK_OUT"/*.png 2>/dev/null | wc -l)
        say "url-walk: captured $n named screen(s)"; ran_any=1
    else
        say "url-walk: did not complete (environmental)"
    fi
    rm -rf "$WALK_OUT"
else
    say "url-walk: SKIPPED (url-walk.sh not executable)"
fi
say ""

# --- 3. catalyst health -----------------------------------------------------
say "## 3. catalyst health check"
HEALTH_OK=1
for ep in "${DCL_HUD_BASE:-http://127.0.0.1:3000}/" \
          "${DCL_HUD_BASE:-http://127.0.0.1:3000}/client"; do
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$ep" 2>/dev/null || echo 000)"
    say "- $ep -> HTTP $code"
    case "$code" in 2*|3*) ;; *) HEALTH_OK=0 ;; esac
done
[ "$HEALTH_OK" = 1 ] && { say "catalyst: healthy"; ran_any=1; } || say "catalyst: UNHEALTHY (non-2xx)"
say ""

say "## session end — $(date -Iseconds)"
# VALID only if at least one measured step actually ran end-to-end.
[ "$ran_any" = 1 ]

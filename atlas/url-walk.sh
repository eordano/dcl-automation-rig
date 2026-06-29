#!/usr/bin/env bash
# =============================================================================
# url-walk.sh — REPLACES the Unity atlas's RunAtlasHeadless reflection driver.
#
# The Unity atlas forced each UI state via a reflection RECIPE (reach interactive,
# then drive the client's auth-FSM into the screen). In the web stack EVERY screen is a
# URL-addressable bevy-overlay route served by catalyst — so "forcing UI state"
# is a chromium navigation. This walks routes.json: for each route, navigate
# headless chromium (under the rig's sway) to $DCL_HUD_BASE + path, wait for a
# settle gate, screenshot, and emit NNN_<route>.png. consolidate-atlas.py then
# renames to <CODE>-<route>.png from atlas-codes.json.
#
# WIRED-NOW: the React HUD routes render REAL catalyst data (SSR), with honest
# per-source fixture fallback (routes.json names each route's source). This is a
# DOM overlay — grim captures it fine (unlike the WebGPU canvas, which is black
# here; see docs/bridge-status.md). So the atlas is the strongest web-stack adapt:
# it actually produces named screenshots of real screens today.
#
# Two passes (auth-capture concept, see auth-capture.md):
#   anon  — routes whose auth=="anon" (the resting/static state; no identity)
#   auth  — routes whose auth=="auth" (need a guest/identity; engine /login_guest
#           is reachable today, full identity GATED on the worker-gap)
# Default walks BOTH; DCL_ATLAS_AUTH=anon|auth restricts.
#
# Usage:
#   url-walk.sh [out_dir]
# Env: DCL_HUD_BASE (catalyst-served origin), DCL_WEB_CDP_PORT,
#      DCL_ATLAS_SETTLE (seconds to settle each route), DCL_ATLAS_AUTH.
# =============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$HERE/../config.sh" 2>/dev/null || true
. "$HERE/../lib/common.sh" 2>/dev/null || true

OUT="${1:-$HOME/dcl/atlas/url-walk}"; RAW="$OUT/_raw"; mkdir -p "$RAW"
BASE="${DCL_HUD_BASE:-http://127.0.0.1:3000}"
CDP_PORT="${DCL_WEB_CDP_PORT:-9344}"
SETTLE="${DCL_ATLAS_SETTLE:-3}"
WANT_AUTH="${DCL_ATLAS_AUTH:-both}"     # anon | auth | both
ONLY="${DCL_ATLAS_ONLY:-}"             # optional comma-separated route subset (re-verify recapture)
ROUTES="$HERE/routes.json"

die() { echo "[url-walk] FATAL: $*" >&2; exit 1; }
[ -f "$ROUTES" ] || die "routes.json not found at $ROUTES"
command -v python3 >/dev/null || die "need python3 to parse routes.json"

# Emit "route<TAB>path<TAB>auth" lines for the requested auth set (and optional
# route subset, for re-verify recapture), in order.
list_routes() {
  python3 - "$ROUTES" "$WANT_AUTH" "$ONLY" <<'PY'
import json, sys
routes = json.load(open(sys.argv[1]))["routes"]
want = sys.argv[2]
only = set(x.strip() for x in sys.argv[3].split(",") if x.strip()) if len(sys.argv) > 3 else set()
for r in routes:
    a = r.get("auth", "anon")
    if want != "both" and a != want:
        continue
    if only and r["route"] not in only:
        continue
    print(f"{r['route']}\t{r['path']}\t{a}")
PY
}

# Settle gate: navigate the page, then wait until the document is interactive and
# the network has been quiet for a beat. We use cdp-capture.py to navigate (via
# DCL_CDP_EVAL = location.replace) and to gate on a readiness sentinel we inject,
# then grim the compositor surface from the host. (DOM overlay -> grim is enough;
# no canvas readback needed.) cdp-capture.py exits on the sentinel or its deadline.
READY_JS='(function(){
  function emit(){ console.log("ATLAS_READY " + location.pathname + location.search); }
  if (document.readyState === "complete") setTimeout(emit, '"$(( SETTLE * 1000 ))"');
  else window.addEventListener("load", function(){ setTimeout(emit, '"$(( SETTLE * 1000 ))"'); });
})();'

# Launch ONE chromium for the whole walk; re-navigate per route over CDP.
"$HERE/../lib/chromium-launch.sh" url "$BASE/client" "$CDP_PORT" atlas \
  || die "chromium launch failed"
for i in $(seq 1 40); do curl -s "http://127.0.0.1:$CDP_PORT/json/version" >/dev/null 2>&1 && break; sleep 1; done

trap 'dcl_pkill_scoped -9 chromium 2>/dev/null || pkill -9 -x chromium 2>/dev/null' EXIT

n=0
while IFS=$'\t' read -r route path auth; do
  [ -z "$route" ] && continue
  n=$((n+1)); nn=$(printf '%03d' "$n")
  url="$BASE$path"
  dcl_log "[$nn] $route ($auth) -> $url"
  # Navigate the existing tab + wait for the injected ATLAS_READY sentinel (or a
  # short deadline), so the screenshot is taken on a settled DOM.
  DCL_CDP_INJECT="$READY_JS" \
  DCL_CDP_EVAL="location.replace('$url')" \
  DCL_CDP_QUIET=1 CDP_PORT="$CDP_PORT" \
    python3 "$HERE/../bevy/cdp-capture.py" "ATLAS_READY" "$(( SETTLE + 12 ))" >/dev/null 2>&1 || true
  # Screenshot the compositor surface (DOM overlay captures fine).
  shot="$RAW/${nn}_${route}.png"
  if command -v grim >/dev/null 2>&1; then
    XDG_RUNTIME_DIR="$DCL_RIG_RT" WAYLAND_DISPLAY=wayland-1 grim "$shot" 2>/dev/null \
      || dcl_log "  grim failed for $route (is the rig sway up? lib/headless-display.sh)"
  else
    dcl_log "  grim absent — skipping screenshot for $route (functional walk only)"
  fi
done < <(list_routes)

dcl_pkill_scoped -9 chromium 2>/dev/null || pkill -9 -x chromium 2>/dev/null || true
trap - EXIT

echo "[url-walk] captured $n route(s) -> $RAW"
# Consolidate into the code-named atlas + refresh the digest.
DCL_ATLAS_OUT="$OUT" python3 "$HERE/consolidate-atlas.py" "$RAW"
DCL_ATLAS_OUT="$OUT" python3 "$HERE/gen-index.py" --digest "$OUT" >/dev/null 2>&1 || true
echo "[url-walk] atlas -> $OUT  (INDEX.md / DIGEST.md regenerated)"

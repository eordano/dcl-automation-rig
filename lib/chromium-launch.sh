#!/usr/bin/env bash
# =============================================================================
# lib/chromium-launch.sh — launch headless chromium onto the rig's GPU sway,
# with the flags the wasm bundle AND the React HUD need. This is the substrate
# web-bench-ab / product-tour-web / atlas url-walk all run on; they spawn a
# browser, then drive it over CDP with bevy/cdp-capture.py.
#
# NEW in the web-stack port (the Unity rig had no browser launcher — it drove a
# Unity client). The flags below are load-bearing and learned the hard way:
#
#   --ozone-platform=x11        chromium presents WebGPU/Vulkan via Xwayland+DRI3
#                               (the proven path; wayland is flakier for the canvas)
#   --enable-unsafe-webgpu      WebGPU is still behind this flag headless
#   --enable-features=Vulkan,SharedArrayBuffer   SAB needs COOP/COEP; the threaded
#                               asset loader needs SAB; WebGPU uses the Vulkan backend
#   --remote-debugging-port     the CDP endpoint cdp-capture.py connects to
#   --remote-allow-origins=*    CDP refuses cross-origin WS handshakes otherwise
#   --user-data-dir=<fresh>     a clean profile per run (no warm GPU cache bias)
#   --no-sandbox                required inside the bwrap namespace
#
# Two modes (the rig drives BOTH the engine console and the HUD route-walk):
#   wasm  — load the engine bundle on a realm/position (the deterministic subject)
#   url   — navigate to an arbitrary catalyst-served HUD route (the atlas walk)
#
# Usage:
#   chromium-launch.sh wasm <realm_url> <position> [cdp_port] [profile_tag]
#   chromium-launch.sh url  <full_url>             [cdp_port] [profile_tag]
# Echoes nothing; backgrounds chromium and returns. Caller waits on the CDP port
# (curl /json/version) then drives cdp-capture.py.
# =============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$HERE/../config.sh" 2>/dev/null || true
. "$HERE/common.sh"

MODE="${1:?usage: chromium-launch.sh <wasm|url> ...}"; shift

# Resolve the DISPLAY of the rig's nested Xwayland (or a real one if set).
disp="${DISPLAY:-}"
if [ -z "$disp" ]; then
  . "$HERE/headless-display.sh" 2>/dev/null || true
  disp="$(dcl_inner_display 2>/dev/null || echo :0)"
fi

case "$MODE" in
  wasm)
    REALM="${1:?wasm needs <realm_url>}"; POS="${2:?wasm needs <position>}"
    CDP="${3:-$DCL_WEB_CDP_PORT}"; TAG="${4:-wasm}"
    URL="http://localhost:$DCL_WEB_PORT/?realm=$REALM&position=$POS"
    ;;
  url)
    URL="${1:?url needs <full_url>}"
    CDP="${2:-$DCL_WEB_CDP_PORT}"; TAG="${3:-hud}"
    ;;
  *) dcl_die "unknown mode '$MODE' (want wasm|url)" ;;
esac

PROF="${DCL_CHROMIUM_PROFILE_ROOT:-$HOME/.cbr-rig}-$TAG"
rm -rf "$PROF"

# Tear down any leftover chromium SCOPED to this rig only (never a co-tenant's).
dcl_pkill_scoped -9 chromium 2>/dev/null || true

dcl_log "chromium[$MODE/$TAG] DISPLAY=$disp CDP=$CDP -> $URL"
"$DCL_SHELL" -c "DISPLAY=$disp chromium \
    --ozone-platform=x11 --start-fullscreen \
    --user-data-dir='$PROF' --no-first-run --no-sandbox \
    --remote-debugging-port=$CDP --remote-allow-origins=* \
    --enable-unsafe-webgpu --enable-features=Vulkan,SharedArrayBuffer \
    '$URL'" >/dev/null 2>&1 &
echo $! > "$PROF.pid"

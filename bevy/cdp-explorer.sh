#!/usr/bin/env bash
# =============================================================================
# cdp-explorer.sh — a PERSISTENT, CDP-driven chromium running the real bevy wasm
# explorer on the rig's GPU (gles2) nested-sway+Xwayland display, for interactive
# testing. Unlike hud-real-scene.sh (one-shot capture + teardown) this stays up:
#   - CDP (remote debugging) on :9344  -> drive it (puppeteer / cdp-drive.mjs)
#   - live view + manual clicking over VNC on :5913 (wayvnc, localhost)
# Reuses the static server (:8080) + CORS proxy (:5142) from serve-explorer.sh.
#
# Env: UI3HUD=1 loads the web-stack React overlay too (default off = bare engine).
#      POSITION=x,y spawn parcel (default 0,0).
# Stop the background task to tear it down (scoped kills only — never the user's
# real chromium).
# =============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$HERE/../config.sh"; . "$HERE/../lib/common.sh"; . "$HERE/../lib/headless-display.sh"
export DCL_SHELL="bash"

PORT="$DCL_WEB_PORT"; PROXY_PORT="$DCL_CATALYST_PROXY_PORT"; CDP="$DCL_WEB_CDP_PORT"
POSITION="${POSITION:-0,0}"
HUDQ=""; [ "${UI3HUD:-0}" = 1 ] && HUDQ="&ui3hud=1"
PROFILE="$HOME/.cbr-rig-cdp"

curl -sf -o /dev/null "http://127.0.0.1:$PORT/index.html" || dcl_die "static server not up on :$PORT — run rig/bevy/serve-explorer.sh first"
curl -sf -o /dev/null "http://127.0.0.1:$PROXY_PORT/about" || dcl_die "CORS proxy not up on :$PROXY_PORT — run rig/bevy/serve-explorer.sh first"

CHROMIUM_PID=""
cleanup() {
  [ -n "$CHROMIUM_PID" ] && { pkill -9 -P "$CHROMIUM_PID" 2>/dev/null; kill -9 "$CHROMIUM_PID" 2>/dev/null; }
  local pid; for pid in $(pgrep -x chromium 2>/dev/null); do
    tr '\0' '\n' < "/proc/$pid/cmdline" 2>/dev/null | grep -q "user-data-dir=$PROFILE" && kill -9 "$pid" 2>/dev/null
  done
  dcl_headless_down 2>/dev/null || true
  rm -rf "$PROFILE" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

dcl_headless_up || dcl_die "GPU display failed to come up"
DISP="$(dcl_inner_display)"
URL="http://localhost:$PORT/?realm=http://127.0.0.1:$PROXY_PORT&position=$POSITION$HUDQ"
rm -rf "$PROFILE"
dcl_log "[cdp-explorer] DISPLAY=$DISP CDP=$CDP -> $URL"

env -u WAYLAND_DISPLAY DISPLAY="$DISP" XDG_RUNTIME_DIR="$DCL_RIG_RT" \
  chromium --ozone-platform=x11 --start-fullscreen \
    --user-data-dir="$PROFILE" --no-first-run --no-sandbox \
    --test-type --disable-infobars --disable-extensions \
    --disable-component-extensions-with-background-pages \
    --no-default-browser-check --disable-default-apps \
    --remote-debugging-port="$CDP" --remote-allow-origins=* \
    --enable-unsafe-webgpu --enable-features=Vulkan,SharedArrayBuffer \
    "$URL" >/tmp/dcl-cdp-chromium.log 2>&1 &
CHROMIUM_PID=$!

for i in $(seq 1 40); do curl -sf -o /dev/null "http://127.0.0.1:$CDP/json/version" && break; sleep 1; done
curl -sf -o /dev/null "http://127.0.0.1:$CDP/json/version" || dcl_die "CDP never came up on :$CDP"
echo "=============================================================="
echo " CDP-driven explorer is UP."
echo "   CDP:   http://127.0.0.1:$CDP   (drive with rig/bevy/cdp-drive.mjs)"
echo "   VNC:   localhost:$DCL_RIG_PORT (watch + click live)"
echo "   URL:   $URL"
echo "   chromium pid=$CHROMIUM_PID  display=$DISP (gles2)"
echo "=============================================================="
wait

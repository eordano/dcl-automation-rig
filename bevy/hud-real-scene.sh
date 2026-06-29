#!/usr/bin/env bash
# =============================================================================
# hud-real-scene.sh — ONE screenshot of the REAL bevy wasm explorer with the
# ui3 React HUD overlay ON TOP of a loaded 3D scene, via the rig's GPU (gles2)
# nested-sway+Xwayland display. Self-contained orchestration with a strict trap.
#
# SAFETY: this host runs the USER'S real interactive chromium on a kwin/Wayland
# desktop. We NEVER use unscoped `pkill chromium`. All browser teardown is
# dcl_pkill_scoped (only our rig's XDG_RUNTIME_DIR). Static server + proxy are
# killed by saved PID. Display is torn down with dcl_headless_down.
# =============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$HERE/../config.sh"
. "$HERE/../lib/common.sh"
. "$HERE/../lib/headless-display.sh"

# dcl-shell is not on PATH here; chromium-launch.sh runs `"$DCL_SHELL" -c ...`.
# Use a plain bash so it just execs chromium directly.
export DCL_SHELL="${DCL_SHELL_OVERRIDE:-bash}"

OUT="${1:-$(cd "$HERE/../.." && pwd)/design/slides/assets/bevy-integration/hud-real-scene.png}"
mkdir -p "$(dirname "$OUT")"

WEB_DIR="$DCL_BEVY_WEB_DIR"
PORT="$DCL_WEB_PORT"           # 8080
CDP="$DCL_WEB_CDP_PORT"        # 9344
PROXY_PORT="$DCL_CATALYST_PROXY_PORT"   # 5142
# Point the proxy at the host that actually serves /about, /content AND /lambdas
# directly (some catalyst /about delegate content+lambdas to a different host that
# the proxy would NOT rewrite).
export DCL_CATALYST_UPSTREAM="${DCL_CATALYST_UPSTREAM_OVERRIDE:-$DCL_CATALYST_UPSTREAM}"
REALM="http://127.0.0.1:$PROXY_PORT"
POSITION="${DCL_TOUR_POSITION:-0,0}"
BOOT_DEADLINE="${BOOT_DEADLINE:-220}"

SERVE_PID=""; PROXY_PID=""; COEP_SERVER=""; CHROMIUM_PID=""
PROFILE="$HOME/.cbr-rig-realscene"   # UNIQUE to this run; the user's browser never uses it

# Kill ONLY chromium procs carrying our unique --user-data-dir. NEVER pkill -x
# chromium (the user runs a real interactive chromium on this host).
kill_my_chromium() {
  local pid pids=""
  for pid in $(pgrep -x chromium 2>/dev/null); do
    if tr '\0' '\n' < "/proc/$pid/cmdline" 2>/dev/null | grep -q "user-data-dir=$PROFILE"; then
      pids="$pids $pid"
    fi
  done
  [ -n "$pids" ] && kill -9 $pids 2>/dev/null || true
}

cleanup() {
  dcl_log "[hud] cleanup: tearing down (precise kills only)"
  [ -n "$CHROMIUM_PID" ] && { pkill -9 -P "$CHROMIUM_PID" 2>/dev/null; kill -9 "$CHROMIUM_PID" 2>/dev/null; }
  kill_my_chromium
  dcl_pkill_scoped -9 chromium 2>/dev/null || true
  [ -n "$SERVE_PID" ] && kill "$SERVE_PID" 2>/dev/null || true
  [ -n "$PROXY_PID" ] && kill "$PROXY_PID" 2>/dev/null || true
  [ -n "$COEP_SERVER" ] && rm -f "$COEP_SERVER" 2>/dev/null || true
  dcl_headless_down 2>/dev/null || true
  rm -rf "$PROFILE" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

# --- 1. GPU display ----------------------------------------------------------
dcl_log "[hud] bringing up nested sway (renderer=$DCL_WLR_RENDERER)"
dcl_headless_up || dcl_die "headless display failed to come up"
dcl_headless_alive || dcl_die "headless display not alive after up"
DISP="$(dcl_inner_display)"
dcl_log "[hud] inner Xwayland DISPLAY=$DISP renderer=$DCL_WLR_RENDERER"

# --- 2. COEP static server for the wasm bundle (in place; no copy) ------------
COEP_SERVER="$(mktemp --suffix=.py)"
cat > "$COEP_SERVER" <<'PYSERVE'
import http.server, sys, os
port, root = int(sys.argv[1]), sys.argv[2]
os.chdir(root)
class H(http.server.SimpleHTTPRequestHandler):
    def end_headers(self):
        self.send_header("Cross-Origin-Opener-Policy", "same-origin")
        self.send_header("Cross-Origin-Embedder-Policy", "require-corp")
        self.send_header("Cross-Origin-Resource-Policy", "cross-origin")
        super().end_headers()
    def log_message(self, *a): pass
http.server.HTTPServer(("127.0.0.1", port), H).serve_forever()
PYSERVE
python3 "$COEP_SERVER" "$PORT" "$WEB_DIR" >/tmp/dcl-hud-serve.log 2>&1 &
SERVE_PID=$!
dcl_wait_for 8 bash -c "curl -s -o /dev/null http://127.0.0.1:$PORT/index.html" \
  || dcl_die "static server did not answer on :$PORT"
dcl_log "[hud] COEP static server pid=$SERVE_PID on :$PORT serving $WEB_DIR"

# --- 3. catalyst CORS/CORP proxy ---------------------------------------------
DCL_CATALYST_PROXY_PORT="$PROXY_PORT" DCL_CATALYST_UPSTREAM="$DCL_CATALYST_UPSTREAM" \
  python3 "$HERE/catalyst-cors-proxy.py" >/tmp/dcl-hud-proxy.log 2>&1 &
PROXY_PID=$!
dcl_wait_for 8 bash -c "curl -s -o /dev/null http://127.0.0.1:$PROXY_PORT/about" \
  || dcl_die "CORS proxy did not answer on :$PROXY_PORT"
dcl_log "[hud] CORS proxy pid=$PROXY_PID on :$PROXY_PORT -> $DCL_CATALYST_UPSTREAM"

# --- 4. launch chromium onto the GPU display ---------------------------------
# IMPORTANT: launch DIRECTLY (not via chromium-launch.sh) so we FORCE the nested
# Xwayland display + the rig's XDG_RUNTIME_DIR. chromium-launch.sh honors a
# pre-set $DISPLAY/$WAYLAND_DISPLAY, and this shell inherits the user's real
# desktop (:0 / wayland-0 / /run/user/1000) — which would land chromium on the
# USER'S kwin desktop instead of our nested sway (and grim would capture black).
DISP="$(dcl_inner_display)"   # ":1" — the nested Xwayland
# ui3hud=1 opts INTO the web-stack React HUD overlay (default OFF in index.html so
# the plain explorer isn't cluttered) — this capture is OF that overlay.
URL="http://localhost:$PORT/?realm=$REALM&position=$POSITION&ui3hud=1"
rm -rf "$PROFILE"
dcl_log "[hud] launching chromium DISPLAY=$DISP (nested) XDG_RUNTIME_DIR=$DCL_RIG_RT -> $URL"
# --disable-extensions: the wrapped chromium on this host auto-installs MetaMask,
# which pops an onboarding window in front of the bevy canvas (grim would capture
# IT). Without an injected wallet the explorer falls back to a guest identity,
# which is fine for rendering a scene. Other flags suppress first-run chrome.
env -u WAYLAND_DISPLAY \
    DISPLAY="$DISP" \
    XDG_RUNTIME_DIR="$DCL_RIG_RT" \
    chromium \
      --ozone-platform=x11 --start-fullscreen \
      --user-data-dir="$PROFILE" --no-first-run --no-sandbox \
      --test-type --disable-infobars \
      --disable-extensions --disable-component-extensions-with-background-pages \
      --no-default-browser-check --disable-default-apps \
      --remote-debugging-port="$CDP" --remote-allow-origins=* \
      --enable-unsafe-webgpu --enable-features=Vulkan,SharedArrayBuffer \
      "$URL" >/tmp/dcl-hud-chromium.log 2>&1 &
CHROMIUM_PID=$!
dcl_log "[hud] chromium pid=$CHROMIUM_PID"

# --- 5. wait for CDP endpoint ------------------------------------------------
for i in $(seq 1 40); do
  curl -s "http://127.0.0.1:$CDP/json/version" >/dev/null 2>&1 && break
  sleep 1
done
curl -s "http://127.0.0.1:$CDP/json/version" >/dev/null 2>&1 \
  || dcl_die "CDP endpoint never came up on :$CDP"
dcl_log "[hud] CDP up on :$CDP"

# --- 6. drive boot + capture via a CDP probe (python, websockets/mini) --------
# Poll over CDP: canvas started? window.engine? scene loaded (live_scenes)?
# Hide boot chrome, then grim-capture a couple frames a few seconds apart.
DCL_CDP_PORT="$CDP" CDP_PORT="$CDP" OUT="$OUT" DISP="$DISP" \
  BOOT_DEADLINE="$BOOT_DEADLINE" RIG_RT="$DCL_RIG_RT" RIG_REPO="$DCL_RIG_REPO" \
  python3 "$HERE/hud-probe.py"
RC=$?

dcl_log "[hud] probe rc=$RC; screenshot -> $OUT"
exit $RC

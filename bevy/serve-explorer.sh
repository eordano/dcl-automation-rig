#!/usr/bin/env bash
# =============================================================================
# serve-explorer.sh — persistently serve the REAL bevy wasm explorer
# (bevy-explorer/deploy/web) so you can open it in YOUR OWN browser (which has
# the GPU/WebGPU). Unlike hud-real-scene.sh this launches NO nested display and
# NO chromium — it just runs the two servers the bundle needs and stays up:
#   1. a COOP/COEP/CORP static server for the wasm bundle (SharedArrayBuffer)
#   2. the catalyst CORS/CORP proxy (so cross-origin content loads under COEP)
#
# Open in your browser:
#   http://localhost:8080/?realm=http://127.0.0.1:5142&position=0,0
#
# Ctrl-C (or stopping the background task) tears both down.
# =============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$HERE/../config.sh"

WEB_DIR="$DCL_BEVY_WEB_DIR"
PORT="${DCL_WEB_PORT:-8080}"
PROXY_PORT="${DCL_CATALYST_PROXY_PORT:-5142}"
# Point upstream at the host that actually serves /content + /lambdas directly.
# (Some catalyst hosts' /about delegate content to a DIFFERENT host, which the
# single-upstream proxy then can't rewrite — content goes direct + COEP/CORS-blocks
# it. Pointing straight at the content-serving host avoids that.)
# Override with EXPLORER_UPSTREAM= to point elsewhere (e.g. a production catalyst).
export DCL_CATALYST_UPSTREAM="${EXPLORER_UPSTREAM:-$DCL_CATALYST_UPSTREAM}"

COEP="$(mktemp --suffix=.py)"
cat > "$COEP" <<'PY'
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
PY

SERVE_PID=""; PROXY_PID=""
cleanup() { [ -n "$SERVE_PID" ] && kill "$SERVE_PID" 2>/dev/null; [ -n "$PROXY_PID" ] && kill "$PROXY_PID" 2>/dev/null; rm -f "$COEP"; }
trap cleanup EXIT INT TERM

python3 "$COEP" "$PORT" "$WEB_DIR" >/tmp/dcl-explorer-serve.log 2>&1 & SERVE_PID=$!
DCL_CATALYST_PROXY_PORT="$PROXY_PORT" python3 "$HERE/catalyst-cors-proxy.py" >/tmp/dcl-explorer-proxy.log 2>&1 & PROXY_PID=$!
sleep 2

echo "=============================================================="
echo " bevy wasm explorer is being served. Open in YOUR browser:"
echo "   http://localhost:$PORT/?realm=http://127.0.0.1:$PROXY_PORT&position=0,0"
echo " (static serve pid=$SERVE_PID :$PORT  |  CORS proxy pid=$PROXY_PID :$PROXY_PORT -> $DCL_CATALYST_UPSTREAM)"
echo "=============================================================="
wait

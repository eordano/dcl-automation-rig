#!/usr/bin/env bash
# =============================================================================
# web-bench-ab.sh — interleaved A/B of two bevy-explorer WASM bundles over CDP.
# The WEB counterpart to bench-ab.sh (native binaries): interleave A,B,A,B in one
# session so both bundles see the same machine state per pair; emit a paired CSV
# for rig/analysis/perf-analyze.py.
#
# >>> ADAPTED (not ported) — and HONESTLY GATED. <<<
# The Unity rig's web-bench-ab.sh measured the engine's deterministic orbit_cpu
# read from the WASMBENCH_RESULT console line, via bevy's OWN benchmark/wasm/
# cdp_capture.py. BOTH are ABSENT in this checkout:
#   - orbit_cpu / WASMBENCH_RESULT only exist in a DCL_WASM_BENCHMARK build
#     (option_env!-gated in web.rs) — see BUILD-WASM-BENCHMARK.md.
#   - bevy's cdp_capture.py is not here — the rig vendors bevy/cdp-capture.py.
# So this script defaults to the submitFps FALLBACK, which PORTS AND WORKS TODAY
# on ANY WebGPU bundle: inject a wrapper around GPUQueue.submit that counts GPU
# submissions per second (a coarse-but-real throughput proxy), capture it over
# CDP, and write it as cpu_ms (perf-analyze is unit-blind). Set DCL_WEB_METRIC=
# orbit to use the orbit_cpu path instead — it will simply MISS (empty pairs) on
# a stock bundle, which is the honest signal that you need a WASMBENCH build.
#
# Usage:
#   web-bench-ab.sh <A_serve_dir> <B_serve_dir> [pairs]
# where each *_serve_dir is a wasm bundle root (deploy/web-shaped: pkg/ etc).
# The script serves each on $DCL_WEB_PORT in turn (COEP) and drives it on the
# local CI realm. Emits CSV for rig/analysis.
# =============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$HERE/../config.sh" 2>/dev/null || true
. "$HERE/../lib/common.sh" 2>/dev/null || true

A_DIR="${1:-${DCL_WEB_A:-}}"
B_DIR="${2:-${DCL_WEB_B:-}}"
PAIRS="${3:-${DCL_WEB_PAIRS:-5}}"
A_LABEL="${DCL_WEB_A_LABEL:-A}"
B_LABEL="${DCL_WEB_B_LABEL:-B}"

METRIC="${DCL_WEB_METRIC:-submitfps}"             # submitfps (works today) | orbit (GATED on WASMBENCH)
BENCH_PORT="${DCL_WEB_PORT:-8080}"
CI_REALM_PORT="${DCL_CI_REALM_PORT:-5199}"
CI_REALM_PATH="${DCL_CI_REALM_PATH:-scene-explorer-tests}"
POSITION="${DCL_WEB_POSITION:-53,-60}"
CDP_PORT="${DCL_WEB_CDP_PORT:-9344}"
CAP_DEADLINE="${DCL_WEB_CAP_DEADLINE:-120}"
CAP="${DCL_CDP_CAPTURE:-$HERE/cdp-capture.py}"     # the VENDORED client, not bevy's absent one
OUT="${DCL_WEB_OUT:-$HOME/dcl/bench/web-ab}"; mkdir -p "$OUT"
CSV="$OUT/orbit-ab.csv"
SERVE_DIR="$HOME/.dcl-web-ab-serve"               # the dir we (re)point at A or B per run

die() { echo "[web-ab] FATAL: $*" >&2; exit 1; }
[ -n "$A_DIR" ] && [ -n "$B_DIR" ] || die "need two bundle dirs: web-bench-ab.sh <A_dir> <B_dir>"
[ -d "$A_DIR" ] || die "A bundle dir missing: $A_DIR"
[ -d "$B_DIR" ] || die "B bundle dir missing: $B_DIR"
[ -f "$CAP" ] || die "cdp-capture.py not found at $CAP"

# Gate on a clean environment unless explicitly skipped.
if [ "${DCL_SKIP_READY:-0}" != 1 ]; then
  "$HERE/measure-ready.sh" || die "environment not ready to measure (DCL_SKIP_READY=1 to override)"
fi

# The submitFps injection: wrap GPUQueue.submit, count calls per second, and
# print one SUBMITFPS line per second. This is the metric that survives without a
# WASMBENCH build — a real GPU-throughput proxy on any WebGPU bundle.
SUBMITFPS_JS='(function(){
  if (!self.GPUQueue || self.__submitfps_wrapped) return;
  self.__submitfps_wrapped = true;
  var orig = self.GPUQueue.prototype.submit, n = 0;
  self.GPUQueue.prototype.submit = function(){ n++; return orig.apply(this, arguments); };
  setInterval(function(){ console.log("SUBMITFPS " + n); n = 0; }, 1000);
})();'

# Serve helper — a COEP/COOP static server on BENCH_PORT pointed at $SERVE_DIR.
# python http.server doesn't add COOP/COEP (needed for SharedArrayBuffer), so we
# write a tiny header-injecting server to a temp file and run it.
COEP_SERVER="$(mktemp --suffix=.py)"
cat > "$COEP_SERVER" <<'PYSERVE'
import http.server, sys, os
port, root = int(sys.argv[1]), sys.argv[2]
os.chdir(root)
class H(http.server.SimpleHTTPRequestHandler):
    def end_headers(self):
        self.send_header("Cross-Origin-Opener-Policy", "same-origin")
        self.send_header("Cross-Origin-Embedder-Policy", "require-corp")
        super().end_headers()
    def log_message(self, *a): pass
http.server.HTTPServer(("127.0.0.1", port), H).serve_forever()
PYSERVE

SERVE_PID=""
serve_dir() { # $1 = bundle dir
  rm -rf "$SERVE_DIR"; cp -r "$1" "$SERVE_DIR"
  [ -n "$SERVE_PID" ] && kill "$SERVE_PID" 2>/dev/null || true
  python3 "$COEP_SERVER" "$BENCH_PORT" "$SERVE_DIR" >/tmp/dcl-web-ab-serve.log 2>&1 &
  SERVE_PID=$!
  sleep 1
}

measure() { # $1 = tag -> echoes "value value" (cpu, gpu) or "" on miss
  dcl_pkill_scoped -9 chromium 2>/dev/null || pkill -9 -x chromium 2>/dev/null; sleep 2
  local realm="http://127.0.0.1:$CI_REALM_PORT/$CI_REALM_PATH"
  "$HERE/../lib/chromium-launch.sh" wasm "$realm" "$POSITION" "$CDP_PORT" "$1" >/dev/null 2>&1
  local i; for i in $(seq 1 40); do curl -s "http://127.0.0.1:$CDP_PORT/json/version" >/dev/null 2>&1 && break; sleep 1; done
  if [ "$METRIC" = orbit ]; then
    # GATED path: orbit_cpu from WASMBENCH_RESULT (empty on a stock bundle).
    local line; line="$(CDP_PORT="$CDP_PORT" python3 "$CAP" WASMBENCH_RESULT "$CAP_DEADLINE" 2>/dev/null | grep -o 'WASMBENCH_RESULT.*' | head -1)"
    echo "$line" | python3 -c '
import sys, json
s = sys.stdin.read(); i = s.find("WASMBENCH_RESULT")
if i < 0: sys.exit(0)
j = s.find("{", i)
try:
    o, _ = json.JSONDecoder().raw_decode(s, j); oc = o["orbit_cpu"]
    print("%.4f %.4f" % (oc["p50"], oc.get("mean", oc["p50"])))
except Exception: pass
'
  else
    # WORKS-TODAY path: median SUBMITFPS over the window (inject the wrapper).
    DCL_CDP_INJECT="$SUBMITFPS_JS" DCL_CDP_QUIET=1 CDP_PORT="$CDP_PORT" \
      python3 "$CAP" SUBMITFPS "$CAP_DEADLINE" 2>/dev/null \
      | grep -o 'SUBMITFPS [0-9]*' | awk '{print $2}' | python3 -c '
import sys, statistics as st
xs = [int(x) for x in sys.stdin.read().split() if x.strip().isdigit()]
xs = [x for x in xs if x > 0]                     # drop the warmup zeros
if xs: print("%.4f %.4f" % (st.median(xs), st.fmean(xs)))
'
  fi
}

echo "drop,window,cond,cpu_ms,gpu_ms" > "$CSV"
echo "[web-ab] metric=$METRIC  $A_LABEL=$A_DIR  $B_LABEL=$B_DIR  pairs=$PAIRS (+1 warmup)" >&2
[ "$METRIC" = orbit ] && echo "[web-ab] NOTE: orbit_cpu is GATED on a DCL_WASM_BENCHMARK build — empty pairs => stock bundle (see BUILD-WASM-BENCHMARK.md)" >&2
WIN=0
trap 'kill "$SERVE_PID" 2>/dev/null; dcl_pkill_scoped -9 chromium 2>/dev/null || pkill -9 -x chromium 2>/dev/null; rm -rf "$SERVE_DIR"; rm -f "$COEP_SERVER"' EXIT

for p in $(seq 0 "$PAIRS"); do
  tag="warmup"; [ "$p" -gt 0 ] && tag="$p"
  serve_dir "$A_DIR"; a="$(measure "a-$tag")"
  serve_dir "$B_DIR"; b="$(measure "b-$tag")"
  if [ "$p" -eq 0 ]; then echo "[web-ab] warmup: $A_LABEL=[$a] $B_LABEL=[$b] (discarded)" >&2; continue; fi
  if [ -z "$a" ] || [ -z "$b" ]; then
    echo "[web-ab] pair $p: MISS ($A_LABEL=[$a] $B_LABEL=[$b]) — if always empty on orbit, the bundle lacks DCL_WASM_BENCHMARK" >&2
    continue
  fi
  WIN=$((WIN+1)); echo "0,$WIN,A,${a% *},${a#* }" >> "$CSV"
  WIN=$((WIN+1)); echo "0,$WIN,B,${b% *},${b#* }" >> "$CSV"
  echo "[web-ab] pair $p: $A_LABEL=${a% *}  $B_LABEL=${b% *}" >&2
done

echo "[web-ab] done. CSV: $CSV" >&2
echo "[web-ab] paired stats:  ./analysis/perf-analyze.py $CSV   (cond $A_LABEL vs $B_LABEL)" >&2

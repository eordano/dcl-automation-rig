#!/usr/bin/env bash
# =============================================================================
# product-tour-web.sh — load the bevy-explorer WASM bundle in headless chromium
# (under the rig's GPU gles2 sway) against the LOCAL scene-explorer-tests realm,
# and classify the product surface in ONE console run. The web analog of the
# Unity atlas+fidelity tours: "does the whole product surface work on this build?"
#
# This is the executable form of bevy/product-tour.md (the live ledger). One load
# at a central parcel pulls in the whole 2x9 feature-scene cluster; a single
# console capture classifies broken / tick / title per scene and panics/wgpu
# errors via cdp_exc.py, emitting a PASS/BROKEN ledger.
#
# >>> HONEST: functional-only. <<<
# There is no GPU window presentation guarantee for the canvas (CDP screenshot +
# grim return black on the WebGPU canvas unless this is a real GPU present path —
# see lib/README.md + docs/bridge-status.md). So this verifies FUNCTIONALLY (the
# scene scripts loaded + ran), NOT by pixels. Per-scene TICK COUNTS + orbit_cpu
# need the WASMBENCH_RESULT.scenes[] structure, which is GATED on a
# DCL_WASM_BENCHMARK build ABSENT here (see BUILD-WASM-BENCHMARK.md). Without it,
# the tour reports load/run/error per scene from the plain console, not counts.
#
# Usage:
#   product-tour-web.sh [out_dir]
# Env: DCL_WEB_PORT (bundle), DCL_CI_REALM_PORT/_PATH (local realm),
#      DCL_TOUR_POSITION (central parcel), DCL_TOUR_DEADLINE (console seconds).
# Gates on bevy/measure-ready.sh GO unless DCL_SKIP_READY=1.
# =============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$HERE/../config.sh" 2>/dev/null || true
. "$HERE/../lib/common.sh" 2>/dev/null || true

OUT="${1:-$HOME/dcl/bench/product-tour}"; mkdir -p "$OUT"
UTC="$(date -u +%Y%m%dT%H%M%SZ)"
LEDGER="$OUT/tour-$UTC.md"
CONSOLE="$OUT/tour-$UTC.console.log"
POSITION="${DCL_TOUR_POSITION:-53,-60}"           # central parcel of the 2x9 cluster
DEADLINE="${DCL_TOUR_DEADLINE:-120}"
CDP_PORT="${DCL_WEB_CDP_PORT:-9344}"
REALM="http://127.0.0.1:${DCL_CI_REALM_PORT:-5199}/${DCL_CI_REALM_PATH:-scene-explorer-tests}"

# The 20 feature scenes (from product-tour.md). Each exercises one SDK feature;
# one cluster load runs them all. We grep the console for each title to classify.
SCENES="Raycast|Transform|Billboard|CameraMode|EngineInfo|Gltf Container|Visibility|Mesh Renderer|Avatar Attach|Material|Text Shape|Video Player|UI-Background|UI-Text|Avatar-Shape|UI-Button|UI-Dropdown|NFT Shape|Basic Controller|UI Scene"

die() { echo "[tour] FATAL: $*" >&2; exit 1; }
[ -d "${DCL_BEVY_WEB_DIR:-/nonexistent}" ] || die "wasm bundle dir not found: $DCL_BEVY_WEB_DIR (set DCL_BEVY_WEB_DIR)"

# Gate on a calm box unless skipped (a contended box skews even functional load).
if [ "${DCL_SKIP_READY:-0}" != 1 ]; then
  "$HERE/measure-ready.sh" || die "environment not ready (DCL_SKIP_READY=1 to override, or --watch measure-ready.sh)"
fi

# 1. Launch chromium onto the rig's GPU sway, loading the bundle on the local realm.
"$HERE/../lib/chromium-launch.sh" wasm "$REALM" "$POSITION" "$CDP_PORT" tour \
  || die "chromium launch failed"

# 2. Wait for the CDP endpoint, then capture the console until the deadline. We
#    use a benign sentinel that won't necessarily appear (scene-load chatter), so
#    the capture streams the FULL run to the deadline; the classification is done
#    on the captured log afterwards. (If a WASMBENCH build is loaded, swap the
#    sentinel to WASMBENCH_RESULT for the structured per-scene tick counts.)
for i in $(seq 1 40); do curl -s "http://127.0.0.1:$CDP_PORT/json/version" >/dev/null 2>&1 && break; sleep 1; done
SENTINEL="${DCL_TOUR_SENTINEL:-WASMBENCH_RESULT}"   # present only on a benchmark build; else streams to deadline
DCL_CDP_QUIET=0 CDP_PORT="$CDP_PORT" \
  python3 "$HERE/cdp-capture.py" "$SENTINEL" "$DEADLINE" > "$CONSOLE" 2>&1 || true

# 3. Tear down the browser SCOPED to this rig only.
dcl_pkill_scoped -9 chromium 2>/dev/null || pkill -9 -x chromium 2>/dev/null || true

# 4. Classify failures (panics/wgpu/closure) from the captured console.
EXC="$("$HERE/cdp_exc.py" "$CONSOLE" 2>&1 || true)"

# 5. Build the per-scene PASS/BROKEN ledger from the plain console (functional).
{
  echo "# Bevy WASM product-surface tour — $UTC"
  echo
  echo "> Functional-only (no pixel check — WebGPU canvas non-present here). Per-scene"
  echo "> TICK COUNTS are GATED on a DCL_WASM_BENCHMARK build (WASMBENCH_RESULT.scenes[])"
  echo "> ABSENT here — see BUILD-WASM-BENCHMARK.md. Realm: $REALM @ $POSITION."
  echo
  echo "## Failure classification (cdp_exc.py)"
  echo '```'
  echo "$EXC"
  echo '```'
  echo
  echo "## Per-scene (console-grep classification)"
  echo "| Scene | Console evidence | Verdict |"
  echo "|---|---|---|"
  IFS='|'
  for scene in $SCENES; do
    # A scene "ran" if its title appears in a load/run line and no error line names it.
    hit="$(grep -iF "$scene" "$CONSOLE" | head -1 | tr -d '|' | cut -c1-80)"
    if [ -n "$hit" ]; then
      verdict="ran"
      grep -iF "$scene" "$CONSOLE" | grep -qiE 'panic|error|broken' && verdict="BROKEN?"
    else
      verdict="no-evidence"
    fi
    echo "| $scene | ${hit:-—} | $verdict |"
  done
  unset IFS
  echo
  echo "_Console: $CONSOLE_"
} > "$LEDGER"

echo "[tour] ledger -> $LEDGER"
echo "[tour] console -> $CONSOLE"
# Exit nonzero if cdp_exc.py found a hard failure (panic / wgpu-validation).
echo "$EXC" | grep -q 'HARD FAILURE' && exit 1 || exit 0

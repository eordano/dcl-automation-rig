#!/usr/bin/env bash
# =============================================================================
# bench-ab.sh — interleaved A/B frame-time benchmark for two NATIVE bevy-explorer
# binaries (decentra-bevy), driving bevy's own benchmark primitives read-only
# WITHOUT modifying that repo. The PORT-LAUNCHER for native-bench-ab.
#
# Why interleave (the whole point, ported verbatim): a back-to-back N-run battery
# bakes thermal/load drift into the A↔B delta. This INTERLEAVES A,B, A,B, … under
# a single bench lock so both binaries see the same machine state on every pair;
# the per-pair delta is what survives. Everything else (warmup, flock, GPU sway,
# aggregate) is reused from the bevy harness.
#
# >>> PORT-LAUNCHER / GATED — read this. <<<
# The interleave mechanism + lock + summarize all port. But the run itself drives
# bevy's OWN benchmark harness (benchmark/ensure-sway.sh, mirror_realm.py,
# aggregate.py, the --benchmark* flags). That harness is NOT present in this
# bevy-explorer checkout. So this launcher is ready, but it GATES on the harness
# existing — build/port benchmark/ into the bevy tree, then re-run. The native 3D
# run also needs a GPU present (the present-needs-a-GPU wall — lib/README.md).
#
# You supply two ALREADY-BUILT binary dirs (one per commit/branch you compare):
#   A_DIR/decentra-bevy  and  B_DIR/decentra-bevy
#
# Usage:
#   bench-ab.sh <A_dir> <B_dir> [pairs]
#   DCL_BENCH_A=~/bins/A DCL_BENCH_B=~/bins/B ./bevy/bench-ab.sh
# Summarize with bevy/bench-summarize.py.
# =============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$HERE/../config.sh" 2>/dev/null || true

REPO="${DCL_BEVY_REPO:-$HOME/projects/bevy-explorer}"
A_DIR="${1:-${DCL_BENCH_A:-}}"
B_DIR="${2:-${DCL_BENCH_B:-}}"
PAIRS="${3:-${DCL_BENCH_PAIRS:-8}}"        # measured pairs (after 1 discarded warmup pair)
A_LABEL="${DCL_BENCH_A_LABEL:-A}"
B_LABEL="${DCL_BENCH_B_LABEL:-B}"

# Measurement knobs — defaults match the bevy harness so A/B numbers are
# comparable to a plain bench.sh run.
FRAMES="${DCL_BENCH_FRAMES:-600}"
LOCATION="${DCL_BENCH_LOCATION:-52,-60}"
DISTANCE="${DCL_BENCH_DISTANCE:-300}"
RES="${DCL_BENCH_RES:-}"                    # e.g. 3840x2160 for a GPU-bound run; empty = harness default
FAKE_PLAYERS="${DCL_BENCH_FAKE_PLAYERS:-0}" # inject N synthetic crowd players for crowd-scaling A/B; 0 = off
PORT="${BENCH_PORT:-${DCL_CI_REALM_PORT:-5199}}"
REALM_DIR="${BENCH_REALM_DIR:-$HOME/dcl/bench/realm}"
RESULTS_ROOT="${BENCH_RESULTS_ROOT:-$HOME/dcl/bench/results}"
DCL_SHELL="${DCL_SHELL:-dcl-shell}"
CPUS="${BENCH_CPUS:-40-47}"
REALM_PATH="${DCL_CI_REALM_PATH:-scene-explorer-tests}"

die() { echo "[bench-ab] FATAL: $*" >&2; exit 1; }
[ -d "$REPO" ] || die "bevy repo not found at $REPO (set DCL_BEVY_REPO)"
[ -n "$A_DIR" ] && [ -n "$B_DIR" ] || die "need two binary dirs: bench-ab.sh <A_dir> <B_dir> (or DCL_BENCH_A/DCL_BENCH_B)"
[ -x "$A_DIR/decentra-bevy" ] || die "no decentra-bevy in A dir: $A_DIR"
[ -x "$B_DIR/decentra-bevy" ] || die "no decentra-bevy in B dir: $B_DIR"
[ -x "$REPO/target/release/dcl_deno_ipc" ] || die "dcl_deno_ipc sidecar missing — build it once: $DCL_SHELL -c 'cd $REPO && cargo build --release --package dcl_deno_ipc'"
[ -x "$REPO/benchmark/ensure-sway.sh" ] || die "GATED: bevy benchmark harness ABSENT at $REPO/benchmark (ensure-sway.sh) — this checkout has no benchmark/. Build/port it, then re-run; the launcher is ready."

RES_A="$RESULTS_ROOT/ab-$A_LABEL"
RES_B="$RESULTS_ROOT/ab-$B_LABEL"
mkdir -p "$RES_A" "$RES_B"
rm -f "$RES_A"/run_*.json* "$RES_A"/warm.json* "$RES_B"/run_*.json* "$RES_B"/warm.json*

# --- Realm mirror + static server (mirrors benchmark/bench.sh; idempotent) ---
# The deterministic benchmark plays against a LOCAL copy of scene-explorer-tests
# so network jitter never enters the frame times. Mirror once, serve on loopback.
if [ ! -f "$REALM_DIR/$REALM_PATH/about" ]; then
    echo "[bench-ab] mirroring realm into $REALM_DIR…"
    python3 "$REPO/benchmark/mirror_realm.py" "$REALM_DIR" "http://localhost:$PORT/" || die "realm mirror failed"
fi
if ! curl -sf "http://localhost:$PORT/$REALM_PATH/about" >/dev/null; then
    echo "[bench-ab] starting realm server on :$PORT"
    nohup python3 -m http.server "$PORT" --bind localhost -d "$REALM_DIR" \
        > /tmp/dcl-bench-realm.log 2>&1 &
    sleep 1
    curl -sf "http://localhost:$PORT/$REALM_PATH/about" >/dev/null \
        || die "realm server failed (see /tmp/dcl-bench-realm.log)"
fi

# --- Serialize against every other bench caller (same lock bench.sh uses) ----
exec 9>/tmp/dcl-bench.lock
echo "[bench-ab] acquiring benchmark lock…"
flock 9
echo "[bench-ab] lock acquired ($A_LABEL vs $B_LABEL)"

if pgrep -f "decentra-bevy" >/dev/null 2>&1; then
    echo "[bench-ab] killing stale decentra-bevy instance(s)"
    pkill -f "decentra-bevy" || true; sleep 3
fi

# Dedicated GPU-rendered headless compositor (NOT a pixman software sway, which
# throttles presents and masks rendering differences). Owned by bevy's harness.
read -r BENCH_RT BENCH_DISPLAY <<< "$("$REPO/benchmark/ensure-sway.sh")"
[ -n "$BENCH_DISPLAY" ] || die "bench sway not available"
echo "[bench-ab] compositor: $BENCH_RT (X on $BENCH_DISPLAY)"

# Crowd scaling: forward --benchmark_fake_players ONLY when asked (>0), so stock
# bevy binaries that lack the flag aren't handed an unknown arg.
FP_ARG=""
[ "$FAKE_PLAYERS" -gt 0 ] 2>/dev/null && FP_ARG="--benchmark_fake_players $FAKE_PLAYERS"

run_one() { # $1 = binary dir, $2 = output json
    timeout 420 taskset -c "$CPUS" env DISPLAY="$BENCH_DISPLAY" XDG_RUNTIME_DIR="$BENCH_RT" \
        WAYLAND_DISPLAY=wayland-1 "$DCL_SHELL" -c \
        "cd $REPO && $1/decentra-bevy \
            --server http://localhost:$PORT/$REALM_PATH \
            --location $LOCATION --distance $DISTANCE \
            --ui none --vsync false --fps 1000 \
            ${RES:+--benchmark_res $RES} $FP_ARG \
            --benchmark $2 --benchmark_frames $FRAMES" \
        > "$2.log" 2>&1
}

echo "[bench-ab] warmup pair (cache fill, discarded)…"
run_one "$A_DIR" "$RES_A/warm.json" || die "A warmup FAILED — see $RES_A/warm.json.log"
run_one "$B_DIR" "$RES_B/warm.json" || die "B warmup FAILED — see $RES_B/warm.json.log"

for i in $(seq 1 "$PAIRS"); do
    n=$(printf '%02d' "$i")
    printf '[bench-ab] pair %d/%d  %s… ' "$i" "$PAIRS" "$A_LABEL"
    run_one "$A_DIR" "$RES_A/run_$n.json" && printf 'ok  %s… ' "$B_LABEL" || printf 'FAIL  %s… ' "$B_LABEL"
    run_one "$B_DIR" "$RES_B/run_$n.json" && echo ok || echo FAIL
done

# Headline aggregation (pooled p90 ms) per side, then the A/B diff.
python3 "$REPO/benchmark/aggregate.py" "$RES_A" >/dev/null
python3 "$REPO/benchmark/aggregate.py" "$RES_B" >/dev/null
echo "[bench-ab] done — A=$RES_A  B=$RES_B"
echo "[bench-ab] compare:  ./bevy/bench-summarize.py '$RES_A' '$RES_B'"

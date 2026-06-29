#!/usr/bin/env bash
# =============================================================================
# conformance.sh — run the scene-explorer-tests conformance battery on ONE native
# bevy-explorer binary and count pass/fail, driving the bevy harness read-only
# WITHOUT modifying that repo. The PORT-LAUNCHER for conformance-native.
#
# Each test scene asserts on its own state and prints a 🟢 (pass) or 🔴 (fail)
# line. This runs a fixed set headless with --scene_log_to_console and counts the
# markers; if the bevy repo ships a baseline, the counts are diffed against it so
# a regression is a non-zero exit, not a number you eyeball.
#
# >>> PORT-LAUNCHER / GATED. <<<
# The mechanism ports verbatim. It drives bevy's OWN benchmark harness
# (ensure-sway.sh, mirror_realm.py) which is NOT present in this checkout — so the
# launcher is ready and GATES on that harness existing. Caveats carried from the
# Unity rig's bevy conformance: the Billboard test can flake, and a snapshot path
# can panic; treat a single red on those as flake-until-confirmed (re-run).
#
# Usage:
#   conformance.sh [bin_dir] [out_log]
#   DCL_BENCH_BIN=~/bins/B ./bevy/conformance.sh
# Scene list overridable (DCL_CONF_SCENES, ';'-separated "x,y" parcels).
# =============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$HERE/../config.sh" 2>/dev/null || true

REPO="${DCL_BEVY_REPO:-$HOME/projects/bevy-explorer}"
BIN_DIR="${1:-${DCL_BENCH_BIN:-$REPO/target/release}}"
OUT="${2:-${DCL_CONF_OUT:-$HOME/dcl/bench/conformance.log}}"
PORT="${BENCH_PORT:-${DCL_CI_REALM_PORT:-5199}}"
REALM_DIR="${BENCH_REALM_DIR:-$HOME/dcl/bench/realm}"
REALM_PATH="${DCL_CI_REALM_PATH:-scene-explorer-tests}"
DCL_SHELL="${DCL_SHELL:-dcl-shell}"
CPUS="${BENCH_CPUS:-40-47}"
BASELINE="${DCL_CONF_BASELINE:-$REPO/benchmark/conformance-baseline.txt}"

# 14-scene default set (the scene-explorer-tests grid around 52,-5x..54,-6x).
SCENES="${DCL_CONF_SCENES:-52,-52;52,-54;52,-56;52,-58;52,-60;52,-62;52,-64;52,-66;52,-68;54,-52;54,-54;54,-56;54,-58;54,-60}"

die() { echo "[conformance] FATAL: $*" >&2; exit 2; }
[ -d "$REPO" ] || die "bevy repo not found at $REPO (set DCL_BEVY_REPO)"
[ -x "$BIN_DIR/decentra-bevy" ] || die "no decentra-bevy in $BIN_DIR (set DCL_BENCH_BIN)"
[ -x "$REPO/benchmark/ensure-sway.sh" ] || die "GATED: bevy benchmark harness ABSENT at $REPO/benchmark — this checkout has no benchmark/. Build/port it, then re-run."
mkdir -p "$(dirname "$OUT")"

# Realm mirror + server (idempotent; mirrors benchmark/bench.sh).
if [ ! -f "$REALM_DIR/$REALM_PATH/about" ]; then
    echo "[conformance] mirroring realm into $REALM_DIR…"
    python3 "$REPO/benchmark/mirror_realm.py" "$REALM_DIR" "http://localhost:$PORT/" || die "realm mirror failed"
fi
if ! curl -sf "http://localhost:$PORT/$REALM_PATH/about" >/dev/null; then
    echo "[conformance] starting realm server on :$PORT"
    nohup python3 -m http.server "$PORT" --bind localhost -d "$REALM_DIR" \
        > /tmp/dcl-bench-realm.log 2>&1 &
    sleep 1
    curl -sf "http://localhost:$PORT/$REALM_PATH/about" >/dev/null \
        || die "realm server failed (see /tmp/dcl-bench-realm.log)"
fi

exec 9>/tmp/dcl-bench.lock
flock 9
echo "[conformance] lock acquired"
pgrep -f "decentra-bevy" >/dev/null 2>&1 && { pkill -f "decentra-bevy" || true; sleep 3; }

read -r BENCH_RT BENCH_DISPLAY <<< "$("$REPO/benchmark/ensure-sway.sh")"
[ -n "$BENCH_DISPLAY" ] || die "bench sway not available"

# --no_fog + --distance 1 keeps it CPU-light and deterministic; the scenes
# self-report, we just need them all loaded and ticking once.
timeout 420 taskset -c "$CPUS" env DISPLAY="$BENCH_DISPLAY" XDG_RUNTIME_DIR="$BENCH_RT" \
    WAYLAND_DISPLAY=wayland-1 "$DCL_SHELL" -c \
    "cd $REPO && $BIN_DIR/decentra-bevy \
        --server http://localhost:$PORT/$REALM_PATH \
        --test_scenes '$SCENES' \
        --no_fog --distance 1 --scene_log_to_console" \
    > "$OUT" 2>&1
rc=$?

green=$(grep -c '🟢' "$OUT" 2>/dev/null || echo 0)
red=$(grep -c '🔴' "$OUT" 2>/dev/null || echo 0)
echo "[conformance] exit=$rc green=$green red=$red  (log: $OUT)"

if [ -r "$BASELINE" ]; then
    b_green=$(grep -o 'green=[0-9]*' "$BASELINE" | head -1 | cut -d= -f2)
    b_red=$(grep -o 'red=[0-9]*' "$BASELINE" | head -1 | cut -d= -f2)
    if [ -n "${b_green:-}" ]; then
        echo "[conformance] baseline: green=$b_green red=${b_red:-?}"
        if [ "$green" -lt "$b_green" ] || { [ -n "${b_red:-}" ] && [ "$red" -gt "$b_red" ]; }; then
            echo "[conformance] REGRESSION vs baseline (re-run once if it's only Billboard/snapshot — known flakes)" >&2; exit 1
        fi
    fi
fi
[ "$red" -eq 0 ] && [ "$green" -gt 0 ]   # nonzero exit if any red or nothing ran

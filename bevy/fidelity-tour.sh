#!/usr/bin/env bash
# =============================================================================
# fidelity-tour.sh — drive bevy-explorer's NATIVE visual-fidelity battery
# read-only, WITHOUT modifying that repo. This is a thin PORT-LAUNCHER: it
# locates the bevy checkout, checks prerequisites, and execs the tour runner that
# already lives there. It never writes into the bevy tree.
#
#   DCL_BEVY_REPO=~/projects/bevy-explorer ./bevy/fidelity-tour.sh [out_dir]
#
# >>> PORT-LAUNCHER / GATED — read this. <<<
# The mechanism ports verbatim from the Unity rig. But it drives bevy's OWN
# fidelity harness (benchmark/fidelity/run-tour.sh + the --benchmark_tour flag),
# which is NOT guaranteed present in THIS checkout (the bevy benchmark/ dir is
# absent here). So:
#   - The LAUNCHER is ported and works the moment the harness exists.
#   - The HARNESS it calls must be present (built/ported) for the tour to run.
# The wasm path verifies the tour FUNCTIONALLY (product-tour-web.sh); the NATIVE
# path here needs a GPU present (the same present-needs-a-GPU wall as everything
# 3D — see lib/README.md). Settle discipline (240-frame floor / readiness gate /
# 1800-frame cap) ports conceptually and is owned by the bevy runner; we don't
# modify the bevy scene battery.
# =============================================================================
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$HERE/../config.sh" 2>/dev/null || true

REPO="${DCL_BEVY_REPO:-$HOME/projects/bevy-explorer}"
OUT="${1:-$HOME/dcl/bench/fidelity}"

RUNNER="$REPO/benchmark/fidelity/run-tour.sh"
BIN="${DCL_BEVY_BIN:-$REPO/target/release/decentra-bevy}"

[ -d "$REPO" ] || { echo "bevy repo not found at $REPO (set DCL_BEVY_REPO)" >&2; exit 1; }
if [ ! -x "$RUNNER" ]; then
    echo "tour runner not found: $RUNNER" >&2
    echo "  GATED: the fidelity battery (benchmark/fidelity/ + the --benchmark_tour" >&2
    echo "  flag) is NOT present in this bevy-explorer checkout. This launcher is" >&2
    echo "  ported and ready; build/port the harness it calls, then re-run. Don't" >&2
    echo "  'git reset/clean' it away if it appears uncommitted in the working tree." >&2
    exit 1
fi
if [ ! -x "$BIN" ]; then
    echo "bevy binary not built: $BIN" >&2
    echo "  Build it first inside your FHS/dcl-shell, e.g.:" >&2
    echo "    \"\${DCL_SHELL:-dcl-shell}\" -c 'cd $REPO && cargo build --release'" >&2
    exit 1
fi

echo "driving bevy fidelity tour (read-only): runner=$RUNNER out=$OUT"
exec "$RUNNER" "$OUT"     # the bevy script owns GPU sway + auto-allow + capture + settle

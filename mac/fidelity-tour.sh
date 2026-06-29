#!/usr/bin/env bash
# =============================================================================
# fidelity-tour.sh — teleport the in-world editor session through a list of
# parcels and screenshot each, via ClaudeIPC. The Unity-EDITOR analogue of
# bevy/fidelity-tour.sh (which drives the external bevy binary). Target: "mac".
#
#   ./mac/fidelity-tour.sh                      # default stops -> $DCL_MAC_SHOTS/tour
#   ./mac/fidelity-tour.sh ~/out                # custom output dir
#   DCL_TOUR_STOPS="0,0 -9,-9 74,-9" ./mac/fidelity-tour.sh
#
# screenshot-game renders Camera.main to a PNG — a clean 3D frame WITHOUT the
# screen-space HUD (cam.Render bypasses the overlay), which is exactly what a
# scene-fidelity gallery wants. For UI screens use mac/atlas-capture.sh instead.
#
# Settle discipline (ported from docs/11): a floor after each teleport, then a
# real world-ready check, then shoot — otherwise you capture a loading frame.
# =============================================================================
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/../config.sh"
. "$HERE/../lib/common.sh"
. "$HERE/../lib/mac-driver.sh"
. "$HERE/../lib/mac-ipc.sh"

[ "$(uname -s)" = Darwin ] || dcl_die "mac/fidelity-tour.sh is macOS-only"

STOPS="${DCL_TOUR_STOPS:-0,0 -9,-9 74,-9}"
OUT="${1:-$DCL_MAC_SHOTS/tour}"; mkdir -p "$OUT"

# Get in-world first (idempotent: editor up → Play → JUMP → Completed).
"$HERE/get-in-world.sh"

dcl_ipc_alive || dcl_die "ClaudeIPC not alive for this project — is the harness compiled and the editor in Play? (see docs/07-mac.md)"
dcl_ipc_wait_world 60 || dcl_die "world not booted (world-ready never true)"
dcl_ipc_clean_hud    # clean captures by default — hide DEBUG PANEL + rewards/notification popups
dcl_log "in-world; touring stops: $STOPS"

i=0
for stop in $STOPS; do
    i=$((i + 1))
    x="${stop%,*}"; y="${stop#*,}"
    dcl_log "stop $i → teleport $x,$y"
    dcl_ipc exec method=DCL.Harness.DclPlaytestHarness.TeleportTo arg.x="$x" arg.y="$y" __timeout=20 >/dev/null \
        || dcl_log "  teleport exec timed out (continuing)"
    sleep "${DCL_TOUR_FLOOR:-12}"          # floor: let the new parcel enqueue + stream GLTFs
    dcl_ipc_wait_world 30 >/dev/null || dcl_log "  world-ready re-check timed out"
    shot="$OUT/tour_${i}_${x}_${y}.png"
    if dcl_ipc screenshot-game path="$shot" width="${DCL_TOUR_W:-1920}" height="${DCL_TOUR_H:-1080}" __timeout=25 | grep -q '"ok":true'; then
        dcl_log "  shot → $shot"
    else
        dcl_log "  screenshot-game failed at $x,$y"
    fi
done
echo "tour done → $OUT"
ls -1 "$OUT"

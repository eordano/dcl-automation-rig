#!/usr/bin/env bash
# =============================================================================
# screenshot.sh — focus the editor and screenshot the display; optional zoom.
# Target: "mac". Brings Unity frontmost first so the capture isn't of whatever
# happened to be on top.
#
#   ./mac/screenshot.sh                       # -> $DCL_MAC_SHOTS/shot.png
#   ./mac/screenshot.sh /tmp/a.png            # explicit path
#   ./mac/screenshot.sh /tmp/a.png --zoom 1600  # then sips-fit to 1600px
#
# To crop into the Game/Scene view instead of zooming the whole frame:
#   sips -c <H> <W> --cropOffset <Y> <X> shot.png --out crop.png
# Click points = screencapture pixel / 2 on a 2x Retina display.
# =============================================================================
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/../config.sh"
. "$HERE/../lib/common.sh"
. "$HERE/../lib/mac-driver.sh"

[ "$(uname -s)" = Darwin ] || dcl_die "mac/screenshot.sh is macOS-only"

OUT="${1:-$DCL_MAC_SHOTS/shot.png}"
dcl_mac_shot "$OUT"
if [ "${2:-}" = "--zoom" ]; then
    sips -Z "${3:-1600}" "$OUT" >/dev/null && dcl_log "zoomed to ${3:-1600}px"
fi
echo "$OUT"

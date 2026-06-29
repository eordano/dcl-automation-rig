#!/usr/bin/env bash
# =============================================================================
# build-player.sh — build the macOS universal (x86_64 + arm64) player from the
# editor in batchmode. Target: "mac" (build half).
#
#   ./mac/build-player.sh           # release
#   ./mac/build-player.sh --dev     # development build
#   DCL_GFX_API=metal ./mac/build-player.sh
#
# Output is a Decentraland.app bundle. Unity produces the universal binary when
# the macOS target's architecture is set to "Intel + Apple Silicon" in the
# project (the default for this project).
# =============================================================================
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/../config.sh"
. "$HERE/../lib/common.sh"

DEV=0; [ "${1:-}" = "--dev" ] && DEV=1
UNITY="$(dcl_unity_bin)"
[ -x "$UNITY" ] || dcl_die "Unity not found at $UNITY (set DCL_UNITY_BIN)"
METHOD=$([ "$DEV" = 1 ] && echo "$DCL_BUILD_MAC_DEV" || echo "$DCL_BUILD_MAC")
LOG="${DCL_BUILD_LOG:-$HOME/build-mac.log}"

export DCL_BUILD_VERSION="${DCL_BUILD_VERSION:-0.0.0-dev}"
dcl_log "building macOS player via $METHOD (log=$LOG)"
"$UNITY" -batchmode -nographics -quit \
    -projectPath "$DCL_PROJECT_DIR" \
    -executeMethod "$METHOD" \
    -buildTarget OSXUniversal \
    -logFile "$LOG"
rc=$?
dcl_log "EXIT $rc"   # 0 ok / 1 build failed / 3 exception (from BuildScript)
exit $rc

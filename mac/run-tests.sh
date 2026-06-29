#!/usr/bin/env bash
# =============================================================================
# run-tests.sh — run Unity EditMode/PlayMode tests in batchmode on macOS.
# Same CI flag set as the Makefile and the Windows runner, so results match.
#
#   ./mac/run-tests.sh editmode
#   ./mac/run-tests.sh playmode DCL.Tests.CodeConventionsTests
# =============================================================================
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/../config.sh"
. "$HERE/../lib/common.sh"

MODE="${1:-editmode}"; FILTER="${2:-}"
RESULTS="${DCL_TEST_RESULTS:-$HOME/test-results}"; mkdir -p "$RESULTS"
UNITY="$(dcl_unity_bin)"

args=(
  -batchmode
  -projectPath "$DCL_PROJECT_DIR"
  -runTests -testPlatform "$MODE"
  -testCategory "${DCL_TEST_CATEGORY:-!Performance}"
  -burst-disable-compilation -accept-apiupdate
  -testResults "$RESULTS/$MODE.xml"
  -logFile "$RESULTS/$MODE.log"
)
# -nographics is fine for EditMode but breaks PlayMode (needs a graphics device).
[ "$MODE" = editmode ] && args=(-nographics "${args[@]}")
[ -n "$FILTER" ] && args+=(-testFilter "$FILTER")

dcl_log "running $MODE tests -> $RESULTS/$MODE.xml"
"$UNITY" "${args[@]}"
echo "EXIT $? results=$RESULTS/$MODE.xml"

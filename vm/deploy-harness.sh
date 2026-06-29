#!/usr/bin/env bash
# =============================================================================
# deploy-harness.sh — push the editor harness sources into the running guest's
# Unity project so it recompiles them in place. Target: "a vm with windows +
# editor".
#
# The guest compiles the harness from its own Assets tree; this scp's the rig's
# vendored copies over the SSH hostfwd so you can iterate on the harness here and
# pick it up on the next reset-and-launch (which nukes ScriptAssemblies, forcing
# a clean recompile). No editor restart logic lives here — just file transfer.
#
#   vm/deploy-harness.sh
#
# Overridable: DCL_VM_* (see vm/vmssh.sh), DCL_VM_GUEST_PROJECT (guest project
# root, forward-slashes), DCL_VM_HARNESS_SUBDIR (Assets-relative dir the harness
# lives in on the guest). FILES below lists what gets pushed.
# =============================================================================
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/../config.sh" 2>/dev/null || true

: "${DCL_VM_KEY:=$HOME/.ssh/win11_dcl}"
: "${DCL_VM_PORT:=2222}"
: "${DCL_VM_USER:=dcl"
: "${DCL_VM_HOST:=localhost}"
: "${DCL_VM_GUEST_PROJECT:=C:/Users/$DCL_VM_USER/unity-explorer/Explorer}"
: "${DCL_VM_HARNESS_SUBDIR:=DCL/Harness/Editor}"

SRC_DIR="$HERE/../unity"
GUEST_DIR="$DCL_VM_GUEST_PROJECT/Assets/$DCL_VM_HARNESS_SUBDIR"

# What to push. The harness + its asmdef are the live-iteration set; add others
# (ClaudeIPC.cs, BuildScript.cs) here if you keep them in the same guest dir.
declare -a FILES=(
  "DclPlaytestHarness.cs"
  "DCL.Harness.Editor.asmdef"
)

for f in "${FILES[@]}"; do
  src="$SRC_DIR/$f"
  if [ ! -f "$src" ]; then echo "SKIP (missing): unity/$f"; continue; fi
  echo "-> $f ($(stat -c%s "$src" 2>/dev/null || echo '?') bytes)"
  scp -q -i "$DCL_VM_KEY" -P "$DCL_VM_PORT" -o StrictHostKeyChecking=accept-new \
      "$src" "$DCL_VM_USER@$DCL_VM_HOST:$GUEST_DIR/$f"
done
echo "DEPLOYED -> $GUEST_DIR"
echo "(next reset-and-launch will recompile it — caches are nuked there)"

#!/usr/bin/env bash
# Mouse click at framebuffer coordinates via the QEMU QMP absolute-tablet input.
# Coords are FRAMEBUFFER pixels (default 1280x800), mapped to QMP's 0..32767 grid
# — not physical screen pixels. Defaults to a single click; -d makes it a double.
#   vm/vmclick.sh 675 61        # single click at (675,61)
#   vm/vmclick.sh -d 400 300    # double click
set -uo pipefail
: "${DCL_VM_DIR:?set DCL_VM_DIR to the VM dir containing qemu-qmp.sock}"
: "${DCL_VM_FB_W:=1280}"; : "${DCL_VM_FB_H:=800}"
QMP="$DCL_VM_DIR/qemu-qmp.sock"
DOUBLE=0; [ "${1:-}" = "-d" ] && { DOUBLE=1; shift; }
X=$(( $1 * 32767 / DCL_VM_FB_W )); Y=$(( $2 * 32767 / DCL_VM_FB_H ))
ev(){ printf '%s\n' "$1"; }
move='{"execute":"input-send-event","arguments":{"events":[{"type":"abs","data":{"axis":"x","value":'$X'}},{"type":"abs","data":{"axis":"y","value":'$Y'}}]}}'
down='{"execute":"input-send-event","arguments":{"events":[{"type":"btn","data":{"button":"left","down":true}}]}}'
up='{"execute":"input-send-event","arguments":{"events":[{"type":"btn","data":{"button":"left","down":false}}]}}'
{
  ev '{"execute":"qmp_capabilities"}'; sleep 0.3
  ev "$move"; sleep 0.2
  ev "$down"; ev "$up"
  [ "$DOUBLE" = 1 ] && { sleep 0.1; ev "$down"; ev "$up"; }
  sleep 0.2
} | ncat -U -w2 "$QMP" >/dev/null 2>&1
echo "CLICK $1,$2 (double=$DOUBLE)"

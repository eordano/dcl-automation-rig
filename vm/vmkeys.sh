#!/usr/bin/env bash
# Out-of-band GUI control of the Windows guest via the QEMU HMP monitor socket.
# Works with no agent inside the guest — screenshots and keystrokes go straight
# to the emulated hardware. Pairs with vm/vmclick.sh (mouse via QMP).
#   vm/vmkeys.sh shot NAME        # screendump -> /tmp/NAME.png (+ size)
#   vm/vmkeys.sh key ret [sleep]  # one sendkey
#   vm/vmkeys.sh type "text"      # type a string then Enter
#   vm/vmkeys.sh login            # type the default user's password + Enter
set -uo pipefail
: "${DCL_VM_DIR:?set DCL_VM_DIR to the VM dir containing qemu-mon.sock}"
MON="$DCL_VM_DIR/qemu-mon.sock"
send(){ printf '%b' "$1" | ncat -U -w2 "$MON" >/dev/null 2>&1; }
case "${1:-}" in
  shot)
    send "screendump /tmp/$2.ppm\n"; sleep 1
    magick "/tmp/$2.ppm" "/tmp/$2.png" 2>/dev/null
    echo "SHOT /tmp/$2.png $(magick identify -format '%wx%h' "/tmp/$2.png" 2>/dev/null)" ;;
  key)   send "sendkey $2\n"; sleep "${3:-1}" ;;
  type)  for ((i=0;i<${#2};i++)); do send "sendkey ${2:$i:1}\n"; sleep 0.15; done; send 'sendkey ret\n'; sleep "${3:-3}" ;;
  login) send 'sendkey ret\n'; sleep 2
         for c in $(echo "${DCL_VM_PASS:-dcl" | grep -o .); do send "sendkey $c\n"; sleep 0.15; done
         send 'sendkey ret\n'; sleep 4 ;;
  *) echo "usage: vmkeys.sh {shot NAME|key K [s]|type STR [s]|login}" >&2; exit 2 ;;
esac
echo "VMKEYS_DONE $*"

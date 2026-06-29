#!/usr/bin/env bash
# SSH into the Windows guest (key auth, host:2222 -> guest:22). Default shell on
# the guest = PowerShell, so pipe scripts in over stdin:
#   echo SCRIPT | vm/vmssh.sh 'powershell -NoProfile -Command -'
#   vm/vmssh.sh 'powershell -NoProfile -Command "Get-Content C:\...\out.json -Raw"'
#
# NOTE: an SSH session lands in Windows session 0 — NO graphics device. Use it
# for file transfer, cache nuking, and *registering* Scheduled Tasks; never to
# launch Unity/the player directly (they need a desktop session — see
# windows/reset-and-launch-editor.ps1).
: "${DCL_VM_KEY:=$HOME/.ssh/win11_dcl}"
: "${DCL_VM_PORT:=2222}"
: "${DCL_VM_USER:=dcl"
: "${DCL_VM_HOST:=localhost}"            # QEMU forwards the guest's :22 to host loopback
exec ssh -i "$DCL_VM_KEY" -p "$DCL_VM_PORT" \
  -o StrictHostKeyChecking=accept-new \
  -o ConnectTimeout=10 -o ServerAliveInterval=15 \
  "$DCL_VM_USER@$DCL_VM_HOST" "$@"

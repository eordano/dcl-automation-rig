#!/usr/bin/env bash
# pr-baseline.sh — measure the screen-health BASELINE of our stack ALONE (auto-fixes, no PR) on
# fresh dev, and write the broken-route set to pr-review-baseline.txt. The PR-review loop subtracts
# this baseline so it only flags PR-caused regressions (not screens already broken at our baseline,
# e.g. atlas drivers stale vs current dev UI). Re-run after re-syncing the stack onto newer dev.
# Shares the loop's lock (.pr-review.lock) so it never collides with a cron tick.
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; VMSSH="$HERE/../vmssh.sh"; KEY="${DCL_VM_KEY:-$HOME/.ssh/win11_dcl}"
LOCK="$HERE/.pr-review.lock"; LOCK_TTL="${PR_REVIEW_TTL:-2700}"
log(){ printf '[%s] %s\n' "$(date -Iseconds)" "$*"; }
pgrep -f qemu-system >/dev/null 2>&1 || { log "VM down"; exit 1; }
if [ -f "$LOCK" ]; then
  age=$(( $(date +%s) - $(stat -c %Y "$LOCK" 2>/dev/null || echo 0) ))
  [ "$age" -lt "$LOCK_TTL" ] && { log "loop busy (lock ${age}s) — try later"; exit 0; }
fi
echo "$$ baseline" > "$LOCK"
cleanup(){ "$VMSSH" "powershell -NoProfile -Command \"Get-Process Unity,UnityShaderCompiler,bee_backend,Unity.Licensing.Client -EA SilentlyContinue | Stop-Process -Force\"" >/dev/null 2>&1; rm -f "$LOCK"; }
trap cleanup EXIT

log "guest -> auto-fixes (our stack, no PR)"
"$VMSSH" 'powershell -NoProfile -Command "cd C:\Users\dcl\unity-explorer; git checkout -f auto-fixes 2>$null; git reset --hard auto-fixes 2>$null"' >/dev/null 2>&1
log "batchmode compile..."
"$VMSSH" 'powershell -NoProfile -Command -' < "$HERE/reset-and-batchcompile.ps1" >/dev/null 2>&1
for i in $(seq 1 40); do
  sleep 30; out=$("$VMSSH" 'powershell -NoProfile -Command -' < "$HERE/poll-batchcompile.ps1" 2>/dev/null)
  u=$(echo "$out"|grep -o 'unity=[0-9]*'|cut -d= -f2); l=$(echo "$out"|grep -o 'log=[0-9]*'|cut -d= -f2)
  log "  compile[$i] $out"; [ "${u:-1}" = 0 ] && [ "${l:-0}" -gt 1000 ] && break
done
log "warm atlas (full)..."
"$VMSSH" 'powershell -NoProfile -Command -' < "$HERE/launch-atlas-warm.ps1" >/dev/null 2>&1
res=""; last=-1; fr=0
for i in $(seq 1 46); do
  sleep 30; out=$("$VMSSH" 'powershell -NoProfile -Command -' < "$HERE/poll-atlas.ps1" 2>/dev/null) || continue
  log "  atlas[$i] $out"; echo "$out"|grep -q json=True && { res=done; break; }
  sz=$(echo "$out"|grep -o 'log=[0-9]*'|cut -d= -f2)
  if [ "${sz:-0}" = "$last" ] && [ "${sz:-0}" -lt 3300000 ]; then fr=$((fr+1)); [ "$fr" -ge 8 ] && { res=stall; break; }; else fr=0; fi
  last="${sz:-0}"
done
[ "$res" = done ] || { log "atlas $res — baseline NOT updated (kept existing)"; exit 1; }
"$VMSSH" 'powershell -NoProfile -Command "Get-Content C:\Users\dcl\harness-report.json -Raw"' > /tmp/baseline-report.json 2>/dev/null
broken=$(PR_REVIEW_BASELINE='' python3 "$HERE/pr-broken.py" /tmp/baseline-report.json)
printf '%s\n' "$broken" > "$HERE/pr-review-baseline.txt"
log "BASELINE = [$broken]  -> $HERE/pr-review-baseline.txt"

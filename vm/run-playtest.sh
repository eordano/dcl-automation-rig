#!/usr/bin/env bash
# =============================================================================
# run-playtest.sh — self-healing editor-harness session inside the Windows VM.
# Target: "a vm with windows + editor".
#
# Drives the full loop from the Linux host: reset+launch the editor harness in
# the guest (windows/reset-and-launch-editor.ps1), then poll the run log and
# babysit it through the two known stall signatures, retrying from scratch:
#   * licensing freeze  — log stops growing well under the play threshold
#   * compile freeze    — same shape, also pre-play
# A run that finishes but never reached the world counts as failed and retries.
#
#   DCL_VM_DIR=... vm/run-playtest.sh [max_attempts]     (default 3)
# =============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/../config.sh" 2>/dev/null || true
VMSSH="$HERE/vmssh.sh"
RESET="$HERE/../windows/reset-and-launch-editor.ps1"
REPORTS="$HERE/reports"; mkdir -p "$REPORTS"

MAX_ATTEMPTS="${1:-3}"
POLL_INTERVAL=30
MAX_POLLS=40            # 20 min budget per attempt
STALL_POLLS=8          # ~4 min frozen pre-play == hang (it never recovers) -> retry fast
PLAY_THRESHOLD=3300000 # log bytes below this == still licensing/compiling

LOG='C:\Users\dcl\harness-run.log'
REPORT='C:\Users\dcl\harness-report.json'

# Inline pollers (kept here so the VM target is self-contained).
poll_ps="\$s=(Get-Item '$LOG' -EA SilentlyContinue).Length; \$j=Test-Path '$REPORT'; Write-Output \"log=\$s json=\$j\""
valid_ps="if(Test-Path '$REPORT'){\$r=Get-Content '$REPORT' -Raw|ConvertFrom-Json; Write-Output \"reached=\$([int]\$r.reachedInteractive)\"}else{Write-Output 'reached=0'}"

for attempt in $(seq 1 "$MAX_ATTEMPTS"); do
  echo "== attempt $attempt/$MAX_ATTEMPTS: reset + launch =="
  "$VMSSH" 'powershell -NoProfile -Command -' < "$RESET"

  last=-1; frozen=0; result=""
  for i in $(seq 1 "$MAX_POLLS"); do
    sleep "$POLL_INTERVAL"
    out=$("$VMSSH" "powershell -NoProfile -Command \"$poll_ps\"" 2>/dev/null) || continue
    echo "   [$i] $out"
    echo "$out" | grep -q 'json=True' && { result=done; break; }
    size=$(echo "$out" | grep -o 'log=[0-9]*' | cut -d= -f2)
    if [ "${size:-0}" = "$last" ] && [ "${size:-0}" -lt "$PLAY_THRESHOLD" ]; then
      frozen=$((frozen+1)); [ "$frozen" -ge "$STALL_POLLS" ] && { result=stall; break; }
    else frozen=0; fi
    last="${size:-0}"
  done

  if [ "$result" = done ]; then
    vout=$("$VMSSH" "powershell -NoProfile -Command \"$valid_ps\"" 2>/dev/null)
    echo "   validation: $vout"
    if echo "$vout" | grep -q 'reached=1'; then
      ts=$(date -u +%Y%m%dT%H%M%SZ)
      "$VMSSH" "powershell -NoProfile -Command \"Get-Content $REPORT -Raw\"" > "$REPORTS/$ts.json"
      echo "== VALID session -> reports/$ts.json =="; exit 0
    fi
    echo "== finished but world never loaded — retrying =="; continue
  fi
  echo "== attempt $attempt ${result:-timed out} — retrying =="
done
echo "== EXHAUSTED $MAX_ATTEMPTS attempts =="; exit 1

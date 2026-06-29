#!/usr/bin/env bash
# =============================================================================
# run-telemetry.sh — run ONE in-editor telemetry/benchmark mode inside the VM,
# fetch its CSV, and hand it to the matching analyzer. Target: "a vm with
# windows + editor".
#
# The harness (unity/DclPlaytestHarness.cs) exposes four measurement modes, each
# a separate `-executeMethod` entry point that writes one CSV. This is the
# host-side driver for all four — same self-healing reset/launch/poll babysitter
# as vm/run-playtest.sh (licensing + compile stalls retried from scratch), just
# waiting on a CSV instead of the session JSON. One parameterized script instead
# of four near-identical copies.
#
#   MODE      entry point                  CSV               analyzer
#   perf      RunPerfHeadless              harness-perf.csv  analysis/perf-analyze.py
#             paired within-session A/B (the only trustworthy kind — see docs/08)
#   cpu       RunCpuBreakdownHeadless      harness-cpu.csv   (printed: ranked ms/frame)
#             every time-marker at steady idle, ranked by per-frame cost
#   render    RunRenderDecompHeadless      harness-render.csv analysis/perf-analyze-multi.py
#             multi-knob render decomposition (shadows/msaa/SRP-batcher/…)
#   shadow    RunShadowPerfHeadless        harness-shadow.csv analysis/perf-analyze.py
#             shadows-on vs shadows-off A/B
#
#   vm/run-telemetry.sh <perf|cpu|render|shadow> [max_attempts]   (default 3)
#
# See docs/16-telemetry-modes.md.
# =============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/../config.sh" 2>/dev/null || true
. "$HERE/../lib/common.sh" 2>/dev/null || true
VMSSH="$HERE/vmssh.sh"
RESET="$HERE/../windows/reset-and-launch-telemetry.ps1"
POLL="$HERE/../windows/poll-telemetry.ps1"
ANALYSIS="$HERE/../analysis"
OUT="$HERE/reports/telemetry"; mkdir -p "$OUT"

MODE="${1:-}"
case "$MODE" in
  perf|cpu|render|shadow) ;;
  *) echo "usage: $0 <perf|cpu|render|shadow> [max_attempts]" >&2; exit 2 ;;
esac
MAX_ATTEMPTS="${2:-3}"

# mode -> guest CSV basename + which analyzer consumes it
case "$MODE" in
  perf)   CSV=harness-perf.csv;   ANALYZER="$ANALYSIS/perf-analyze.py" ;;
  cpu)    CSV=harness-cpu.csv;    ANALYZER="" ;;            # printed, not stats-tested
  render) CSV=harness-render.csv; ANALYZER="$ANALYSIS/perf-analyze-multi.py" ;;
  shadow) CSV=harness-shadow.csv; ANALYZER="$ANALYSIS/perf-analyze.py" ;;
esac

# SSH/scp coordinates — same env contract as vm/vmssh.sh.
: "${DCL_VM_KEY:=$HOME/.ssh/win11_dcl}"
: "${DCL_VM_PORT:=2222}"
: "${DCL_VM_USER:=dcl"
: "${DCL_VM_HOST:=localhost}"
GUEST_CSV="C:/Users/$DCL_VM_USER/$CSV"

POLL_INTERVAL=30
MAX_POLLS=40            # 20 min budget per attempt
STALL_POLLS=8          # ~4 min frozen pre-play == hang -> retry fast
PLAY_THRESHOLD=3300000 # log bytes below this == still licensing/compiling

# Pipe a ps1 over SSH with $Mode pre-set (piped scripts can't take -params, so we
# prepend the assignment; the ps1 reads $Mode with a fallback, no param() block).
launch_ps() { { printf "\$Mode='%s'\n" "$MODE"; cat "$1"; } | "$VMSSH" 'powershell -NoProfile -Command -'; }

for attempt in $(seq 1 "$MAX_ATTEMPTS"); do
  echo "== $MODE attempt $attempt/$MAX_ATTEMPTS: reset + launch =="
  launch_ps "$RESET"

  last=-1; frozen=0; result=""
  for i in $(seq 1 "$MAX_POLLS"); do
    sleep "$POLL_INTERVAL"
    out=$(launch_ps "$POLL" 2>/dev/null) || continue
    echo "   [$i] $out"
    echo "$out" | grep -q 'csv=True' && { result=done; break; }
    size=$(echo "$out" | grep -o 'log=[0-9]*' | cut -d= -f2)
    if [ "${size:-0}" = "$last" ] && [ "${size:-0}" -lt "$PLAY_THRESHOLD" ]; then
      frozen=$((frozen+1)); [ "$frozen" -ge "$STALL_POLLS" ] && { result=stall; break; }
    else frozen=0; fi
    last="${size:-0}"
  done

  if [ "$result" = done ]; then
    ts=$(date -u +%Y%m%dT%H%M%SZ)
    dest="$OUT/$MODE-$ts.csv"
    scp -q -i "$DCL_VM_KEY" -P "$DCL_VM_PORT" -o StrictHostKeyChecking=accept-new \
        "$DCL_VM_USER@$DCL_VM_HOST:$GUEST_CSV" "$dest" 2>/dev/null
    echo "== $MODE CSV -> $dest =="
    if [ -n "$ANALYZER" ] && [ -f "$ANALYZER" ]; then
      python3 "$ANALYZER" "$dest"
    else
      command -v column >/dev/null 2>&1 && column -t -s, "$dest" | head -75 || head -75 "$dest"
    fi
    exit 0
  fi
  echo "== $MODE attempt $attempt ${result:-timed out} — retrying =="
done
echo "== $MODE: exhausted $MAX_ATTEMPTS attempts =="; exit 1

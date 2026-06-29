#!/usr/bin/env bash
# =============================================================================
# measure-ready.sh — is THIS the right moment to take a measurement?
#
# A benchmark number is only comparable to another taken under the same machine
# state. The same wasm bundle can report materially different fps/CPU depending
# on what else the host is doing, whether the headless GPU compositor is up, and
# whether a stray browser is still holding the GPU. This script samples that
# state and prints a single GO / WAIT verdict so you can gate a run on it:
#
#   ./bevy/measure-ready.sh || { echo "skipping — not ready"; exit 0; }
#
# It is advisory and cheap (no side effects). Thresholds are env-overridable.
# Exit 0 = GO, exit 1 = WAIT. Use --quiet to suppress the report (verdict only),
# --watch to block until GO (polls every DCL_READY_POLL seconds, up to
# DCL_READY_TIMEOUT).
#
# Signals sampled:
#   - 1-minute load average / nproc  (the dominant predictor of run-to-run noise)
#   - headless GPU sway present + reachable (the bench display; a missing/SW
#     compositor silently changes frame pacing — see docs/03 + docs/13)
#   - leftover chromium processes (a previous run not torn down steals the GPU)
#   - GPU utilisation, if nvidia-smi is present (a busy GPU = a contended run)
# =============================================================================
set -uo pipefail

NPROC="$(nproc)"
# GO if 1-min loadavg per core is below this. 0.5 is conservative for a perf A/B;
# raise it for coarse go/no-go, lower it for tight sub-5% comparisons.
MAX_LOAD_PER_CORE="${DCL_READY_MAX_LOAD_PER_CORE:-0.5}"
MAX_GPU_UTIL="${DCL_READY_MAX_GPU_UTIL:-40}"          # percent; only checked if nvidia-smi exists
SWAY_DISPLAY_FILE="${DCL_BENCH_SWAY_DISPLAY_FILE:-/run/user/$(id -u)/dcl-bench-sway/display}"
POLL="${DCL_READY_POLL:-20}"
TIMEOUT="${DCL_READY_TIMEOUT:-600}"

QUIET=0; WATCH=0
for a in "$@"; do
  case "$a" in
    --quiet) QUIET=1 ;;
    --watch) WATCH=1 ;;
  esac
done

assess() {
  local reasons=() ok=1

  # --- load average -------------------------------------------------------
  local load1; load1="$(awk '{print $1}' /proc/loadavg)"
  local per_core; per_core="$(awk -v l="$load1" -v n="$NPROC" 'BEGIN{printf "%.3f", l/n}')"
  local load_ok; load_ok="$(awk -v p="$per_core" -v m="$MAX_LOAD_PER_CORE" 'BEGIN{print (p<m)?1:0}')"
  [ "$load_ok" = 1 ] || { ok=0; reasons+=("load/core ${per_core} >= ${MAX_LOAD_PER_CORE} (load1 ${load1} on ${NPROC} cores)"); }

  # --- headless GPU compositor -------------------------------------------
  local sway="absent"
  if [ -r "$SWAY_DISPLAY_FILE" ]; then
    local disp; disp="$(cat "$SWAY_DISPLAY_FILE" 2>/dev/null)"
    if pgrep -x sway >/dev/null 2>&1; then sway="up ($disp)"; else sway="display-file-but-no-sway"; ok=0; reasons+=("bench sway not running"); fi
  else
    sway="no-display-file"; ok=0; reasons+=("no bench sway display file ($SWAY_DISPLAY_FILE)")
  fi

  # --- leftover browsers --------------------------------------------------
  # pgrep -c prints "0" yet exits 1 when nothing matches, so don't `|| echo 0`
  # (that would double the count); just capture and default an empty result.
  local chromium_n; chromium_n="$(pgrep -xc chromium 2>/dev/null)"; chromium_n="${chromium_n:-0}"
  [ "$chromium_n" -eq 0 ] || { ok=0; reasons+=("$chromium_n leftover chromium proc(s) — tear down first"); }

  # --- GPU utilisation (optional) ----------------------------------------
  local gpu="n/a"
  if command -v nvidia-smi >/dev/null 2>&1; then
    gpu="$(nvidia-smi --query-gpu=utilization.gpu --format=csv,noheader,nounits 2>/dev/null | head -1 | tr -d ' ')"
    if [ -n "$gpu" ] && [ "$gpu" -gt "$MAX_GPU_UTIL" ] 2>/dev/null; then
      ok=0; reasons+=("GPU util ${gpu}% > ${MAX_GPU_UTIL}%")
    fi
    gpu="${gpu}%"
  fi

  if [ "$QUIET" -ne 1 ]; then
    printf 'load1=%s (%.3f/core, max %s)  sway=%s  chromium=%s  gpu=%s\n' \
      "$load1" "$per_core" "$MAX_LOAD_PER_CORE" "$sway" "$chromium_n" "$gpu" >&2
    if [ "$ok" -ne 1 ]; then printf 'WAIT: %s\n' "$(IFS='; '; echo "${reasons[*]}")" >&2; else echo "GO" >&2; fi
  fi
  return $(( ok == 1 ? 0 : 1 ))
}

if [ "$WATCH" -eq 1 ]; then
  start=$SECONDS
  while :; do
    if assess; then exit 0; fi
    [ $(( SECONDS - start )) -ge "$TIMEOUT" ] && { echo "measure-ready: timed out after ${TIMEOUT}s still not ready" >&2; exit 1; }
    sleep "$POLL"
  done
else
  assess
fi

#!/usr/bin/env bash
# =============================================================================
# convert/unity-batch-watchdog.sh — kill a HUNG headless Unity.
#
# Why this exists: headless Unity batchmode wedges — license-server stalls,
# import deadlocks, GC death-spirals — and just sits there at ~0% CPU. The
# per-entity `timeout` in convert-loop.sh eventually frees it, but waiting out a
# 2h timeout on a run that died in minute 3 wastes the whole budget. This
# watchdog watches CPU and kills a *stalled* run fast, so the loop's `timeout`
# is only the last resort. Applies to any batchmode Unity (conversions, repeated
# BuildScript builds, EditMode test sweeps) — not just the converter.
#
# Run it alongside the loop:   ./convert/unity-batch-watchdog.sh &
# It exits on its own when the watched driver process is gone.
#
# Optional env:
#   WATCH_WHILE   pattern of the driver to babysit; watchdog exits when no such
#                 process remains (default: convert-loop.sh). Set to "" to run
#                 until killed (e.g. when babysitting an ad-hoc build).
#   STUCK_SECS    kill after this long under the CPU floor (default: 300)
#   CPU_FLOOR     percent below which Unity counts as stalled (default: 1)
#   SAMPLE_SECS   CPU sampling window (default: 3)
#   POLL_SECS     loop period (default: 60)
# =============================================================================
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$HERE/../lib/common.sh"

WATCH_WHILE="${WATCH_WHILE-convert-loop.sh}"
STUCK_SECS="${STUCK_SECS:-300}"
CPU_FLOOR="${CPU_FLOOR:-1}"
SAMPLE_SECS="${SAMPLE_SECS:-3}"
POLL_SECS="${POLL_SECS:-60}"

# Match the batchmode EDITOR specifically. Matching the bare version path (e.g.
# `6000.2.6f2/Editor/Unity`) also matches the persistent Unity.Licensing daemon,
# which is always near 0% CPU — you'd kill the wrong thing every tick. The FHS
# wrapper forks the real editor, so the pid we want is the `-batchmode` child,
# not the launcher; find it by command line.
unity_pid() { pgrep -f 'Editor/Unity .*-batchmode' | head -1; }

dcl_log "watchdog: start (watch='${WATCH_WHILE:-<forever>}' stuck=${STUCK_SECS}s floor=${CPU_FLOOR}%)"
prev_pid=""
prev_time=0
while [ -z "$WATCH_WHILE" ] || pgrep -f "$WATCH_WHILE" >/dev/null 2>&1; do
    UPID="$(unity_pid)"
    if [ -z "$UPID" ]; then
        prev_pid=""; prev_time=0
        sleep "$POLL_SECS"; continue
    fi

    # top -n2 because the first sample is a meaningless since-boot average; the
    # second is the live window. Column 9 (%CPU) of the matching pid row.
    cpu=$(top -b -n 2 -p "$UPID" -d "$SAMPLE_SECS" 2>/dev/null \
            | awk -v p="$UPID" '$1==p{c=$9} END{print c+0}')
    now=$(date +%s)

    # New pid (a fresh entity started) → reset the stall clock.
    if [ "$UPID" != "$prev_pid" ]; then
        prev_pid="$UPID"; prev_time=$now
        dcl_log "watchdog: tracking Unity pid=$UPID cpu=${cpu}%"
    fi

    cpu_int=${cpu%.*}
    if [ "${cpu_int:-0}" -lt "$CPU_FLOOR" ] && [ $((now - prev_time)) -gt "$STUCK_SECS" ]; then
        dcl_log "watchdog: KILL pid=$UPID — stuck $(( (now-prev_time)/60 ))min at ${cpu}% cpu"
        # Kill the whole tree. On a multi-Unity host this targets the stalled pid
        # plus the FIRST matching helper of each kind (faithful to the original);
        # if you run several batchmode Unitys at once, scope these by cgroup/uid.
        TIMEOUT_PID=$(pgrep -f 'timeout .*Unity'      2>/dev/null | head -1)
        BWRAP_PID=$(pgrep -f 'bwrap.*Unity'           2>/dev/null | head -1)
        LIC_PID=$(pgrep -f 'Unity.Licensing.Client'   2>/dev/null | head -1)
        UPM_PID=$(pgrep -f 'UnityPackageManager'      2>/dev/null | head -1)
        kill -KILL $UPID $TIMEOUT_PID $BWRAP_PID $LIC_PID $UPM_PID 2>/dev/null
        prev_pid=""; prev_time=0
        sleep 10
    fi
    sleep "$POLL_SECS"
done
dcl_log "watchdog: exit (driver '${WATCH_WHILE}' gone)"

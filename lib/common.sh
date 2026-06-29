# shellcheck shell=bash
# =============================================================================
# lib/common.sh — tiny shared helpers (logging, polling, process scoping).
# Sourced after config.sh. Keep this dependency-free and side-effect-free.
#
# PORTED verbatim from the Unity rig — these are platform-agnostic and the
# scoped-kill safety property is exactly what lets many web rigs share one host.
# The only Unity-specific thing dropped was a boot-tweak helper; nothing here
# mentioned Unity, so the lib ports as-is.
# =============================================================================

dcl_log()  { printf '[%s] %s\n' "$(date -Iseconds)" "$*" >&2; }
dcl_die()  { dcl_log "FATAL: $*"; exit 1; }

# Poll until CMD succeeds or TIMEOUT seconds elapse. Returns 0/1.
#   dcl_wait_for <timeout-s> <cmd...>
dcl_wait_for() {
    local timeout="$1"; shift
    local deadline=$(( $(date +%s) + timeout ))
    while [ "$(date +%s)" -lt "$deadline" ]; do
        "$@" && return 0
        sleep 0.5
    done
    return 1
}

# Scoped process kill — only signals processes whose environ carries the SAME
# XDG_RUNTIME_DIR as the current rig. This is the key safety property that lets
# many rigs share one host: an unscoped `pkill -f chromium` would nuke every
# session's browser (and a co-tenant's perf run); this never reaches outside
# $DCL_RIG_RT. The web rig leans on this HARD — every web capability spawns a
# chromium, and several may run in parallel on different rig ports.
#   dcl_pkill_scoped [-SIGNAL] <pattern>
dcl_pkill_scoped() {
    local sig="TERM"
    case "$1" in -*) sig="${1#-}"; shift ;; esac
    local pat="$1" scope="${DCL_RIG_RT:-}"
    [ -n "$scope" ] || { dcl_log "pkill_scoped: no DCL_RIG_RT, refusing"; return 1; }
    local pid
    for pid in $(pgrep -f "$pat" 2>/dev/null); do
        if tr '\0' '\n' < "/proc/$pid/environ" 2>/dev/null | grep -qx "XDG_RUNTIME_DIR=$scope"; then
            kill -"$sig" "$pid" 2>/dev/null || true
        fi
    done
}

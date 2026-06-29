# shellcheck shell=bash
# =============================================================================
# lib/mac-ipc.sh — shell client for the in-editor ClaudeIPC server
# (unity/ClaudeIPC.cs). Sourced after config.sh + common.sh. Writes a command
# JSON to /tmp/dcl-editor/cmd/ and reads the reply from /tmp/dcl-editor/out/.
#
# The Mac analogue of the Linux rig's dcl-editor-ipc.py. Every command is stamped
# project=$DCL_PROJECT_DIR so the right editor claims it when several share ROOT.
# Mac is the first host that both presents a window AND runs ClaudeIPC, so this
# is where exec/world-ready/ui-*/screenshot-game actually get exercised.
# =============================================================================

: "${DCL_IPC_ROOT:=/tmp/dcl-editor}"

# Per-project heartbeat filename — mirror ClaudeIPC.ProjectHeartbeat()'s sanitize
# EXACTLY (every non-alphanumeric char -> '_'), or we'd watch the wrong file.
dcl_ipc_hb_file() {
    printf '%s/hb-%s\n' "$DCL_IPC_ROOT" "$(printf '%s' "$DCL_PROJECT_DIR" | sed 's/[^A-Za-z0-9]/_/g')"
}

# Is THIS project's editor IPC alive? (heartbeat touched within ~5s)
dcl_ipc_alive() {
    local hb; hb="$(dcl_ipc_hb_file)"
    [ -f "$hb" ] || return 1
    # mtime epoch, portable: GNU `stat -c %Y` (this host's nix coreutils) OR BSD
    # `stat -f %m`. (`stat -f` means filesystem-stat under GNU — don't use it.)
    local m; m="$(stat -c %Y "$hb" 2>/dev/null || stat -f %m "$hb" 2>/dev/null || echo 0)"
    [ $(( $(date +%s) - m )) -le 5 ]
}

# dcl_ipc <op> [key=value ...] [__timeout=SECONDS]
# Sends {"op":..,"project":..,key:value,...}; prints the reply JSON; 1 on timeout.
# Values are sent as JSON strings (ClaudeIPC.ParseArg coerces to the param type),
# so int/bool args go as e.g. arg.x=140.
dcl_ipc() {
    local op="$1"; shift
    local timeout=15 json="{\"op\":\"$op\",\"project\":\"$DCL_PROJECT_DIR\"" kv k v
    for kv in "$@"; do
        case "$kv" in __timeout=*) timeout="${kv#__timeout=}"; continue ;; esac
        k="${kv%%=*}"; v="${kv#*=}"
        v="${v//\\/\\\\}"; v="${v//\"/\\\"}"   # escape \ and " for JSON
        json="$json,\"$k\":\"$v\""
    done
    json="$json}"
    mkdir -p "$DCL_IPC_ROOT/cmd" 2>/dev/null || true
    local id="m$(date +%s)$$$RANDOM" out
    printf '%s' "$json" > "$DCL_IPC_ROOT/cmd/$id.json"
    out="$DCL_IPC_ROOT/out/$id.json"
    local deadline=$(( $(date +%s) + timeout ))
    while [ "$(date +%s)" -lt "$deadline" ]; do
        if [ -f "$out" ]; then cat "$out"; rm -f "$out"; return 0; fi
        sleep 0.3
    done
    dcl_log "ipc timeout after ${timeout}s: $op"
    return 1
}

# Hide the dev DEBUG PANEL overlay (right-edge) so captures are clean BY DEFAULT.
# No-op if the IPC isn't up. Call once the world is booted (it reads staticContainer).
dcl_ipc_hide_debug() {
    dcl_ipc_alive 2>/dev/null || return 0
    dcl_ipc exec method=DCL.Harness.DclPlaytestHarness.HideDebugPanel __timeout=10 >/dev/null 2>&1 || true
}

# Hide the transient rewards / notification toast popups (NewNotificationPanel).
# No-op if the IPC isn't up. Call once the world is booted.
dcl_ipc_hide_rewards() {
    dcl_ipc_alive 2>/dev/null || return 0
    dcl_ipc exec method=DCL.Harness.DclPlaytestHarness.HideRewardsPopup __timeout=10 >/dev/null 2>&1 || true
}

# Hide everything that clutters a capture (debug panel + rewards/notification popup).
dcl_ipc_clean_hud() { dcl_ipc_hide_debug; dcl_ipc_hide_rewards; }

# Convenience: wait until world-ready reports world:true (editor booted in-world).
#   dcl_ipc_wait_world [timeout_s]
dcl_ipc_wait_world() {
    local timeout="${1:-180}" deadline=$(( $(date +%s) + timeout )) r
    while [ "$(date +%s)" -lt "$deadline" ]; do
        r="$(dcl_ipc world-ready __timeout=8 2>/dev/null || true)"
        case "$r" in *'"world":true'*) return 0 ;; esac
        sleep 2
    done
    return 1
}

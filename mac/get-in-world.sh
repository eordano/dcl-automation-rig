#!/usr/bin/env bash
# =============================================================================
# get-in-world.sh — drive the running GUI editor into Play, click "JUMP INTO
# DECENTRALAND", wait for the world to finish loading, and screenshot the
# rendered Game view. Target: "mac".
#
# This is the render-verify the Linux/VM targets CANNOT do: both reach the GUI
# step then die with no GPU presentation. Mac presents via Metal, so this is the
# one place the rig proves the explorer actually *renders*.
#
#   ./mac/get-in-world.sh
#   DCL_MAC_JUMP_FRAC=0.66 ./mac/get-in-world.sh     # nudge the JUMP click point
#   DCL_MAC_JUMP_X=720 DCL_MAC_JUMP_Y=560 ./mac/get-in-world.sh   # absolute click
# =============================================================================
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/../config.sh"
. "$HERE/../lib/common.sh"
. "$HERE/../lib/mac-driver.sh"
. "$HERE/../lib/mac-ipc.sh"

[ "$(uname -s)" = Darwin ] || dcl_die "mac/get-in-world.sh is macOS-only"

# Ensure the editor is up (idempotent — reuses a running one).
"$HERE/editor-up.sh"
LOG="$DCL_MAC_LOG"
dcl_mac_keepawake
mkdir -p "$DCL_MAC_SHOTS"

# Line baseline: detection looks only PAST this point, so a previous Play
# session's markers can't produce a false positive. Anchor on lines, not the
# log's (skewed) timestamps.
base="$(wc -l < "$LOG" 2>/dev/null | tr -d ' ')"; : "${base:=1}"

dcl_log "entering Play (Cmd+P)"
dcl_mac_play
# Verify Play actually started: keystrokes get SWALLOWED if sent mid compile/
# import, so wait for the auth/loading screen marker and re-send Cmd+P once.
if ! dcl_mac_wait_log "$LOG" "$DCL_MARK_AUTH" "${DCL_MAC_PLAY_TIMEOUT:-240}" "$base"; then
    dcl_log "no loading screen yet — re-sending Cmd+P (compile may have eaten it)"
    dcl_mac_play
    dcl_mac_wait_log "$LOG" "$DCL_MARK_AUTH" "${DCL_MAC_PLAY_TIMEOUT:-240}" "$base" \
        || dcl_die "editor never reached the JUMP/loading screen (see $LOG)"
fi
dcl_log "reached the JUMP-INTO-DECENTRALAND loading screen"
dcl_mac_shot "$DCL_MAC_SHOTS/01-jump-screen.png"

# Click "JUMP INTO DECENTRALAND". Prefer ClaudeIPC ui-click (matches the button
# by LABEL — robust, no pixel hunting, no missed CGEvents) when the harness IPC is
# live; fall back to a real CGEvent pixel click (UGUI ignores osascript clicks).
# The boot PARKS here until clicked. NOTE the auth FSM is stateful: the cached
# "Welcome G → JUMP" quick-resume only shows with a fresh cached identity — else
# you land on the login *selection* (METAMASK/GOOGLE/email) and there's no JUMP
# button. Pass DCL_MAC_APP_ARGS=--skip-auth-screen (via editor-up) for the
# consistent cached-login path. ("Curl error 42" in the log is a red herring.)
clicked=0
if dcl_ipc_alive 2>/dev/null; then
    if dcl_ipc ui-click text="${DCL_JUMP_LABEL:-JUMP INTO DECENTRALAND}" __timeout=10 2>/dev/null | grep -q '"ok":true'; then
        dcl_log "clicked JUMP via IPC ui-click (by label)"; clicked=1
    else
        dcl_log "IPC ui-click found no JUMP button (login selection showing?) — trying pixel click"
    fi
fi
if [ "$clicked" = 0 ]; then
    if ! read -r jx jy <<<"$(dcl_mac_jump_point)"; then
        dcl_die "no JUMP via IPC and can't compute a click point. If the screen shows the login SELECTION (not 'Welcome → JUMP'), relaunch with DCL_MAC_APP_ARGS=--skip-auth-screen; else set DCL_MAC_JUMP_X/Y."
    fi
    dcl_log "clicking JUMP at logical ${jx},${jy} (CGEvent; override DCL_MAC_JUMP_X/Y)"
    dcl_mac_click "$jx" "$jy"
fi

# After the click it runs ProfileLoading → … → Completed in seconds.
if dcl_mac_wait_log "$LOG" "$DCL_MARK_COMPLETED" "${DCL_MAC_WORLD_TIMEOUT:-300}" "$base"; then
    sleep "${DCL_MAC_SETTLE:-6}"   # let the first in-world frames present
    dcl_ipc_clean_hud             # hide the dev DEBUG PANEL + rewards/notification popups
    dcl_mac_shot "$DCL_MAC_SHOTS/02-in-world.png"
    dcl_log "WORLD COMPLETED — rendered shot: $DCL_MAC_SHOTS/02-in-world.png"
    echo "OK in-world: $DCL_MAC_SHOTS/02-in-world.png"
else
    dcl_mac_shot "$DCL_MAC_SHOTS/02-stuck.png"
    dcl_die "world did not reach '$DCL_MARK_COMPLETED' (shot: 02-stuck.png; see $LOG). If it's stuck at login, the JUMP click may have missed — recalibrate DCL_MAC_JUMP_FRAC/X/Y."
fi

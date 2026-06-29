# shellcheck shell=bash
# =============================================================================
# lib/mac-driver.sh — drive the *real* GUI Unity editor on macOS: focus,
# keystrokes, hardware clicks, screenshots, log-watching. Sourced after
# config.sh + common.sh. macOS only; keep it dependency-free and side-effect-free.
#
# This is the Mac analogue of lib/headless-display.sh. The crucial difference:
# this host CAN present a GPU window (Metal), so unlike the Linux/VM targets we
# drive the genuine rendered editor — no nested display, no IPC file dance.
# Every trick below is load-bearing; the comment is the part you can't re-derive.
# =============================================================================

# AppKit identifies the editor by its bundle name — the process is "Unity"
# (Unity Hub is a separate "Unity Hub" process we must never target).
: "${DCL_MAC_PROC:=Unity}"

# Keep the display awake. A slept Retina panel yields an all-black screencapture,
# so any unattended capture run must hold sleep off. -dimsu = display+idle+sys+usb.
dcl_mac_keepawake() {
    pgrep -f 'caffeinate -dimsu' >/dev/null 2>&1 && return 0
    caffeinate -dimsu & disown
    dcl_log "caffeinate -dimsu running (keeps screencapture from going black)"
}

# Bring the editor frontmost. CRITICAL: plain `activate` is NOT enough — AppKit
# only routes keystrokes to the process whose `frontmost` is set true. Every
# keystroke helper calls this first.
dcl_mac_focus() {
    osascript -e "tell application \"System Events\" to set frontmost of process \"$DCL_MAC_PROC\" to true" 2>/dev/null || true
}

# Pid of the EDITOR bound to our project — not Unity Hub, not another project's
# editor. grep -F (fixed strings) so a path with regex-special chars can't bite.
dcl_mac_editor_pid() {
    ps -ax -o pid=,command= 2>/dev/null \
      | grep -F "Unity.app/Contents/MacOS/Unity" \
      | grep -F -- "-projectPath $DCL_PROJECT_DIR" \
      | grep -vF "Unity Hub" \
      | awk '{print $1; exit}'
}

# Send a keystroke to the editor. Focus first — keys are SWALLOWED if the editor
# isn't frontmost (and also during a compile/import, hence the re-send pattern in
# get-in-world.sh).  dcl_mac_key <key> [modifier-clause]
#   dcl_mac_key r "command down"     # Cmd+R
dcl_mac_key() {
    local key="$1" mods="${2:-}"
    dcl_mac_focus
    if [ -n "$mods" ]; then
        osascript -e "tell application \"System Events\" to keystroke \"$key\" using {$mods}"
    else
        osascript -e "tell application \"System Events\" to keystroke \"$key\""
    fi
}

# Cmd+R = asset import + script compile. Auto-Refresh is often OFF, so editing a
# .cs does NOT recompile on focus — force it before entering Play.
dcl_mac_recompile() { dcl_log "Cmd+R — force asset import + script compile"; dcl_mac_key r "command down"; }
# Cmd+P toggles Play. From Play it stops; from Edit it enters Play.
dcl_mac_play()      { dcl_log "Cmd+P — toggle Play"; dcl_mac_key p "command down"; }
dcl_mac_stop()      { dcl_mac_play; }

# Real hardware click (CGEvent via the vendored Swift helper). UGUI ignores
# osascript clicks — see mac/click.swift. Coords are LOGICAL points.
dcl_mac_click() {
    local x="$1" y="$2"
    [ -r "$DCL_MAC_CLICK_SWIFT" ] || { dcl_log "click.swift missing at $DCL_MAC_CLICK_SWIFT"; return 1; }
    swift "$DCL_MAC_CLICK_SWIFT" "$x" "$y"
}

# Unity window 1 bounds as "x y w h" (logical points, top-left origin) — for
# calibrating click points. Returns empty if the editor has no window yet.
dcl_mac_window_bounds() {
    local b
    b="$(osascript -e "tell application \"System Events\" to tell process \"$DCL_MAC_PROC\" to return position of window 1 & size of window 1" 2>/dev/null)" || return 1
    printf '%s\n' "${b//,/}"   # AppleScript joins with ", " — drop the commas
}

# Logical click point for the red "JUMP INTO DECENTRALAND" button.
#
# RELIABLE path: set absolute DCL_MAC_JUMP_X / DCL_MAC_JUMP_Y. Get them once by
# screenshotting the JUMP screen (mac/screenshot.sh), cropping to the button
# (sips), and halving the pixel center for the 2x Retina logical coord — e.g.
# this host calibrated to 1134,378 (window 637,33 875x949).
#
# FALLBACK: a fraction of window 1. This only lands if the Game view fills the
# window (Game tab "Maximize On Play"); in a multi-panel layout the button sits
# inside the Game *sub-panel*, not at a fixed window fraction, so prefer X/Y.
dcl_mac_jump_point() {
    if [ -n "${DCL_MAC_JUMP_X:-}" ] && [ -n "${DCL_MAC_JUMP_Y:-}" ]; then
        printf '%s %s\n' "$DCL_MAC_JUMP_X" "$DCL_MAC_JUMP_Y"; return
    fi
    local wx wy ww wh
    read -r wx wy ww wh <<<"$(dcl_mac_window_bounds)" || return 1
    [ -n "${wh:-}" ] || return 1
    awk -v x="$wx" -v y="$wy" -v w="$ww" -v h="$wh" \
        -v fx="${DCL_MAC_JUMP_FRAC_X:-0.5}" -v fy="${DCL_MAC_JUMP_FRAC:-0.62}" \
        'BEGIN{ printf "%d %d\n", x + w*fx, y + h*fy }'
}

# Screenshot the display to a PNG (editor frontmost first; -x = no shutter sound;
# -D selects the display so a multi-monitor host still grabs the right one).
dcl_mac_shot() {
    local out="$1"
    mkdir -p "$(dirname "$out")"
    dcl_mac_focus; sleep 0.3
    screencapture -x -D "${DCL_MAC_DISPLAY:-1}" "$out"
    dcl_log "shot -> $out"
}

# Wait until fixed-string PAT appears in FILE at/after line FROM, or TIMEOUT s.
# Anchor on a LINE baseline, never timestamps — the editor log clock can be
# skewed hours from system time (see global notes). Returns 0/1.
#   dcl_mac_wait_log <file> <pattern> [timeout=120] [from-line=1]
dcl_mac_wait_log() {
    local file="$1" pat="$2" timeout="${3:-120}" from="${4:-1}"
    local deadline=$(( $(date +%s) + timeout ))
    while [ "$(date +%s)" -lt "$deadline" ]; do
        if [ -r "$file" ] && tail -n +"$from" "$file" 2>/dev/null | grep -qF "$pat"; then return 0; fi
        sleep 2
    done
    return 1
}

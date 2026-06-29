#!/usr/bin/env bash
# =============================================================================
# binary-proton.sh — run the WINDOWS player build on Linux via GE-Proton.
# Target: "linux binary" (Proton path — for the upstream refclient .exe when no
# native Linux build exists, or to compare against the shipped Windows binary).
#
#   DCL_WIN_EXE=/path/Decentraland.exe ./linux/binary-proton.sh [-- extra args]
#
# Must run on the host (not inside the rig's bwrap): steam-run needs real /sys.
# It still renders into the rig's nested Xwayland over the network namespace.
# =============================================================================
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/../config.sh"
. "$HERE/../lib/common.sh"
. "$HERE/../lib/headless-display.sh"

EXE="${DCL_WIN_EXE:?set DCL_WIN_EXE to the extracted Decentraland.exe}"
PFX="${DCL_PROTON_PFX:-$HOME/.dcl-rig/proton-pfx}"   # Wine prefix (persists license/login)
PROTON="${DCL_PROTON_PATH:?set DCL_PROTON_PATH to a GE-Proton dir (has ./proton)}"

dcl_headless_up

# steam-run runs Proton inside an FHS sandbox that does NOT expose /tmp — a Wine
# prefix or url-log under /tmp is invisible to Proton and fails at prefix-lock
# time. Keep them under a real $HOME path (the config defaults already do).
case "$PFX$DCL_URL_LOG" in
    /tmp/*|*:/tmp/*) dcl_log "WARN: $PFX or url-log is under /tmp — steam-run won't see it; use a \$HOME path" ;;
esac
mkdir -p "$PFX"

export PROTONPATH="$PROTON" WINEPREFIX="$PFX"
export STEAM_COMPAT_DATA_PATH="$PFX" STEAM_COMPAT_CLIENT_INSTALL_PATH="" SteamGameId=0
# UMU_ID=0 makes Proton use umu.exe instead of steam.exe, dodging lsteamclient's
# "!status" assertion that fires when no Steam client is running.
export UMU_ID=0
# Disable nvapi (unused, can hang) and lsteamclient. Disabling lsteamclient is
# what makes Application.OpenURL() route through winebrowser -> our xdg-open
# shim instead of returning EAGAIN — i.e. it's what makes headless auth work.
export WINEDLLOVERRIDES="nvapi,nvapi64=,lsteamclient="
export DXVK_FRAME_RATE="${DCL_DXVK_FPS:-60}"
export DISPLAY="$(dcl_inner_display)"
export XDG_RUNTIME_DIR="$DCL_RIG_RT"
export DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$(id -u)/bus"
export PATH="$DCL_RIG_REPO/auth:$PATH"          # url-capture shim
export URL_LOG="$DCL_URL_LOG"

# Wine looks up the browser via this registry key BEFORE PATH, so also point it
# at the shim (absolute path stops execvp from re-searching PATH and finding the
# host xdg-open). Idempotent: only injected once.
USER_REG="$PFX/pfx/user.reg"
if [ -f "$USER_REG" ] && ! grep -q 'Software\\\\Wine\\\\WineBrowser' "$USER_REG"; then
    cat >> "$USER_REG" <<REG

[Software\\\\Wine\\\\WineBrowser] 1774238072
"Browsers"="$DCL_RIG_REPO/auth/xdg-open"
"Mailers"="$DCL_RIG_REPO/auth/xdg-open"
REG
fi

# DCL_WALK_FORCE_X11=1 hides WAYLAND_DISPLAY so Wine uses x11.drv — needed if you
# drive input with xdotool/XTEST (the wayland driver ignores those).
[ "${DCL_WALK_FORCE_X11:-0}" = "1" ] && unset WAYLAND_DISPLAY

dcl_log "launching Windows player under GE-Proton: $EXE"
cd "$(dirname "$EXE")"
# steam-run provides the FHS sandbox Proton needs (real /sys etc.). Prefer one
# already on PATH; otherwise pull it via nix-shell — which needs the unfree flag
# because steam-unwrapped is unfree.
GAME_ARGS="-screen-width ${DCL_SCREEN_W:-1280} -screen-height ${DCL_SCREEN_H:-720} -screen-fullscreen 0 $* ${DCL_PLAYER_EXTRA_ARGS:-}"
if command -v steam-run >/dev/null 2>&1; then
    exec steam-run "$PROTON/proton" run "$EXE" $GAME_ARGS
else
    export NIXPKGS_ALLOW_UNFREE=1
    exec nix-shell -p steam-run --run "steam-run '$PROTON/proton' run '$EXE' $GAME_ARGS"
fi

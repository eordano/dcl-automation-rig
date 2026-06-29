#!/usr/bin/env bash
# =============================================================================
# editor-up.sh — launch the Unity Editor headless on Linux, into the rig's
# nested display, with auth capture and crash-retry. Target: "linux editor".
#
#   ./linux/editor-up.sh            # bring rig up if needed, then launch editor
#
# Drive the running editor over the file-IPC in unity/ClaudeIPC.cs
# (write JSON to /tmp/dcl-editor/cmd/, read /tmp/dcl-editor/out/). See
# docs/01-linux-editor.md.
# =============================================================================
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/../config.sh"
. "$HERE/../lib/common.sh"
. "$HERE/../lib/headless-display.sh"
. "$HERE/../lib/auth.sh"

# Seed the Unity license + Hub state from the persisted snapshot, so a headless
# or fresh box can license the editor. Seed-only: cp -an never clobbers a live
# license, so an already-activated box is untouched. After a machine-binding
# change the seeded license won't validate and Unity needs an online Hub
# re-activation (then re-snapshot). See docs/01-linux-editor.md.
_restore_unity_config() {
    local p="$DCL_UNITY_PERSIST"
    [ -d "$p" ] || return 0
    mkdir -p "$HOME/.config/unity3d" "$HOME/.local/share/unity3d" "$HOME/.config/UnityHub"
    cp -an "$p/config-unity3d/."  "$HOME/.config/unity3d/"      2>/dev/null || true
    cp -an "$p/share-unity3d/."   "$HOME/.local/share/unity3d/" 2>/dev/null || true
    cp -an "$p/config-unityhub/." "$HOME/.config/UnityHub/"     2>/dev/null || true
    dcl_log "seeded Unity license/config from $p"
}

# Clear crash/lock state that would otherwise wedge a headless boot behind a
# modal ("Recover backup scene?", a leftover bug-reporter, a stale lockfile).
_clean_stale_state() {
    rm -f "$DCL_PROJECT_DIR/Temp/UnityLockfile" 2>/dev/null || true
    rm -rf "$DCL_PROJECT_DIR/Temp/__Backupscenes" "$DCL_PROJECT_DIR/Library/CurrentScene" 2>/dev/null || true
    pkill -f 'UnityBugReporter' 2>/dev/null || true
}

dcl_headless_up
_restore_unity_config
_clean_stale_state

# Avoid the LifecycleController boot NRE (see disable-test-asmdefs.sh).
"$HERE/disable-test-asmdefs.sh" "$DCL_PROJECT_DIR" || true

UNITY="$(dcl_unity_bin)"
[ -x "$UNITY" ] || dcl_die "Unity not found at $UNITY (set DCL_UNITY_BIN)"
LOG="$DCL_RIG_LOG_DIR/editor.log"

# X11 display from the nested Xwayland; auth-capture shim on PATH.
export DISPLAY="$(dcl_inner_display)"
export WAYLAND_DISPLAY=wayland-1
export XDG_RUNTIME_DIR="$DCL_RIG_RT"
eval "$(dcl_auth_shim_env)"

# Graphics API for the editor. Default Vulkan; set DCL_EDITOR_GFX=-force-glcore
# on a headless software display with no DRI3 (Vulkan inits a device but can't
# *present* a window to the pixman-backed Xwayland and the editor exits at layout
# load). OpenGL via llvmpipe presents over Xwayland without DRI3. See docs/03.
GFX="${DCL_EDITOR_GFX:--force-vulkan}"
dcl_log "launching Unity editor (display=$DISPLAY, gfx=$GFX, log=$LOG)"
attempt=0
until [ "$attempt" -ge "${DCL_EDITOR_RETRIES:-4}" ]; do
    attempt=$((attempt + 1))
    # $DCL_FHS_WRAP is an FHS provider on NixOS (unityhub-fhs-env/steam-run),
    # empty elsewhere. Unquoted so an empty value vanishes instead of becoming argv[0].
    $DCL_FHS_WRAP "$UNITY" \
        -projectPath "$DCL_PROJECT_DIR" \
        $GFX \
        -logFile "$LOG" \
        ${DCL_EDITOR_EXTRA_ARGS:-} &
    editor_pid=$!
    # Warmup WATCH: poll the whole window (not "until first healthy"), so an early
    # exit or a crash signature partway through is actually caught and retried —
    # the boot NRE is transient and a relaunch usually clears it.
    crashed=0; waited=0; warmup="${DCL_EDITOR_WARMUP:-75}"
    while [ "$waited" -lt "$warmup" ]; do
        sleep 5; waited=$((waited + 5))
        if ! kill -0 "$editor_pid" 2>/dev/null; then crashed=1; break; fi
        if grep -Eq 'Aborting batchmode|Lifecycle ERROR : Cannot exit scope' "$LOG" 2>/dev/null; then crashed=1; break; fi
    done
    if [ "$crashed" = 0 ]; then
        dcl_log "editor survived warmup (pid=$editor_pid, attempt=$attempt)"
        echo "$editor_pid" > "$DCL_RIG_RT/editor.pid"
        exit 0
    fi
    dcl_log "boot failed (attempt $attempt) — retrying"
    kill "$editor_pid" 2>/dev/null || true; sleep 2
done
dcl_die "editor failed to boot after $attempt attempts (see $LOG)"

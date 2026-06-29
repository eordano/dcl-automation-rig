# shellcheck shell=bash
# =============================================================================
# lib/headless-display.sh — a headless GPU display you can screenshot and drive
# over VNC, with no real monitor attached. Linux only. The substrate EVERY web
# capability (web-cdp-capture, product-tour-web, web-bench-ab, atlas url-walk)
# runs on.
#
# Stack:  bubblewrap → nested sway (wlroots, headless backend) → wayvnc
#   - sway runs with WLR_BACKENDS=headless so it renders to memory, not a screen.
#   - wayvnc exports that surface over VNC on $DCL_RIG_PORT.
#   - Xwayland is force-started inside sway: chromium runs --ozone-platform=x11
#     onto it (the WebGPU/Vulkan + DRI3 path is the proven one — wayland is
#     flakier for the GPU canvas).
#
# >>> ADAPTED from the Unity rig with ONE load-bearing change: require GPU gles2,
#     NOT pixman. <<<
# The Unity rig defaulted WLR_RENDERER=pixman (pure CPU; ran anywhere; fine for
# Unity boot/license/compile because that work is non-GUI). The web rig CANNOT
# use pixman: a WebGPU canvas on a software/pixman compositor captures BLACK
# (no DRI3 presentation path) and pixman paces presents to a low frame rate,
# destroying frame timing. So real WebGPU capture + frame timing need a
# GPU-backed sway with DRI3 — DCL_WLR_RENDERER defaults to gles2 in config.sh.
# Everything else (wayvnc export, forced Xwayland, the bwrap /tmp/.X11-unix bind,
# PipeWire/pulse sockets, dcl_headless_shot) is kept verbatim.
# =============================================================================

# Run a command string that needs CLI tools, providing them portably:
# prefer nix-shell (maps binary names to nixpkgs attrs); on a non-nix host fall
# back to running directly if the tools are already on PATH.
#   dcl_with_tools <bin>... -- "<command string>"
dcl_with_tools() {
    local bins=() b cmd
    while [ "${1:-}" != "--" ]; do bins+=("$1"); shift; done
    shift; cmd="$1"
    if command -v nix-shell >/dev/null 2>&1; then
        local pkgs=()
        for b in "${bins[@]}"; do case "$b" in
            Xwayland) pkgs+=(xwayland) ;; bwrap) pkgs+=(bubblewrap) ;; *) pkgs+=("$b") ;;
        esac; done
        nix-shell -p "${pkgs[@]}" --run "$cmd"
    else
        for b in "${bins[@]}"; do command -v "$b" >/dev/null 2>&1 \
            || dcl_die "need '$b' on PATH (or nix-shell) for the headless display"; done
        bash -c "$cmd"
    fi
}

# WHY bubblewrap: the host's /tmp/.X11-unix is owned by nobody:nogroup
# (systemd-tmpfiles), and wlroots refuses to start Xwayland against that
# ownership. We bind a user-owned dir over /tmp/.X11-unix *inside the namespace
# only* — the host is untouched.
dcl_headless_up() {
    local port="${DCL_RIG_PORT}" rt="${DCL_RIG_RT}"
    local priv_x11="$DCL_RIG_TMP/x11-private"
    local log="$DCL_RIG_LOG_DIR/sway.log"
    local conf="${DCL_SWAY_CONF:-$DCL_RIG_REPO/lib/sway-session.conf}"

    mkdir -p "$rt" "$priv_x11" "$DCL_RIG_LOG_DIR"
    chmod 700 "$rt"; chmod 1777 "$priv_x11"

    if dcl_headless_alive; then dcl_log "rig :$port already up"; return 0; fi

    # WLR_RENDERER: gles2 = GPU (config.sh default). A pixman/software value here
    # makes the WebGPU canvas capture black and ruins frame timing — refuse it
    # loudly so a measurement is never silently taken on the wrong compositor.
    local renderer="${DCL_WLR_RENDERER:-gles2}"
    if [ "$renderer" = pixman ]; then
        dcl_log "WARN: DCL_WLR_RENDERER=pixman — WebGPU canvas will capture BLACK and frame timing is meaningless. Use gles2/vulkan on a real GPU (see lib/README.md)."
    fi

    dcl_log "starting nested sway+wayvnc (port=$port rt=$rt renderer=$renderer)"
    local launch="
      bwrap \
        --bind / / --proc /proc --dev-bind /dev /dev \
        --bind '$priv_x11' /tmp/.X11-unix \
        --setenv XDG_RUNTIME_DIR '$rt' \
        --setenv WLR_BACKENDS headless \
        --setenv WLR_RENDERER '$renderer' \
        --setenv WLR_LIBINPUT_NO_DEVICES 1 \
        --setenv DCL_VNC_PORT '$port' \
        --setenv DCL_VNC_BIND '$DCL_VNC_BIND' \
        --share-net \
        -- sway -c '$conf' -d
    "
    dcl_with_tools sway wayvnc Xwayland bwrap -- "$launch" >"$log" 2>&1 &
    echo $! > "$rt/sway.pid"

    dcl_wait_for 30 dcl_headless_alive || dcl_die "sway/wayvnc did not come up (see $log)"
    dcl_audio_up
    dcl_log "rig :$port ready — VNC at $DCL_VNC_BIND:$port (renderer=$renderer)"
}

# Liveness: the IPC socket must answer, not merely exist (stale sockets from a
# dead nested-sway are the classic "everything silently no-ops" failure mode).
dcl_headless_alive() {
    local sock
    sock="$(ls "$DCL_RIG_RT"/sway-ipc.*.sock 2>/dev/null | head -1)" || return 1
    [ -n "$sock" ] && SWAYSOCK="$sock" swaymsg -t get_version >/dev/null 2>&1
}

# Run a command inside the rig's sway (it inherits the wayland + X11 display).
dcl_headless_exec() {
    local sock
    sock="$(ls "$DCL_RIG_RT"/sway-ipc.*.sock 2>/dev/null | head -1)"
    SWAYSOCK="$sock" swaymsg exec -- "$@"
}

# Screenshot the headless surface from the host (grim talks to wayland-1).
# NOTE: grim captures the COMPOSITOR surface — DOM overlays (the React HUD, the
# loading screen) capture fine, but the WebGPU <canvas> itself returns black
# unless this is a GPU (gles2) sway. See docs/bridge-status.md + product-tour.
dcl_headless_shot() {
    local out="${1:-/tmp/dcl-rig-shot.png}"
    dcl_with_tools grim -- "XDG_RUNTIME_DIR='$DCL_RIG_RT' WAYLAND_DISPLAY=wayland-1 grim '$out'"
    dcl_log "shot -> $out"
}

# The nested Xwayland DISPLAY (":N"), discovered from the private X11 socket dir.
# chromium exports this with --ozone-platform=x11 (see lib/chromium-launch.sh).
dcl_inner_display() {
    local n
    n=$(ls "$DCL_RIG_TMP/x11-private" 2>/dev/null | sed -n 's/^X\([0-9]\+\)$/\1/p' | sort -n | head -1)
    [ -n "$n" ] && echo ":$n" || echo ":1"
}

# Audio: PipeWire + pulse shim inside the sway namespace. Idempotent.
# The wasm bundle's LiveKit voice path expects a sound server at process start —
# so this is part of "up", not optional (same reason as the Unity rig's FMOD).
dcl_audio_up() {
    [ -S "$DCL_RIG_RT/pulse/native" ] && return 0
    dcl_headless_exec env "XDG_RUNTIME_DIR=$DCL_RIG_RT" pipewire
    dcl_wait_for 4 test -S "$DCL_RIG_RT/pipewire-0"
    dcl_headless_exec env "XDG_RUNTIME_DIR=$DCL_RIG_RT" wireplumber
    dcl_headless_exec env "XDG_RUNTIME_DIR=$DCL_RIG_RT" pipewire-pulse
    dcl_wait_for 4 test -S "$DCL_RIG_RT/pulse/native" || dcl_log "WARN: pulse socket not up"
}

dcl_headless_down() {
    dcl_pkill_scoped -TERM 'sway -c'
    rm -f "$DCL_RIG_RT/sway.pid"
    dcl_log "rig :$DCL_RIG_PORT down"
}

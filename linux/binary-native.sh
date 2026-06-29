#!/usr/bin/env bash
# =============================================================================
# binary-native.sh — run the native Linux player build headless on the rig.
# Target: "linux binary" (native, not Proton — for the Proton path see
# binary-proton.sh).
#
#   ./linux/binary-native.sh [-- extra explorer args]
#
# Expects a Linux build produced by unity/BuildScript.BuildLinux64[Dev]
# (default: <repo>/build/Linux/decentraland-explorer.x86_64).
# =============================================================================
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/../config.sh"
. "$HERE/../lib/common.sh"
. "$HERE/../lib/headless-display.sh"

BIN="${DCL_LINUX_BIN:-$DCL_EXPLORER_REPO/build/Linux/decentraland-explorer.x86_64}"
[ -x "$BIN" ] || dcl_die "Linux player not found at $BIN (build it, or set DCL_LINUX_BIN)"

dcl_headless_up
export DISPLAY="$(dcl_inner_display)"
export WAYLAND_DISPLAY=wayland-1
export XDG_RUNTIME_DIR="$DCL_RIG_RT"

# --- NixOS / glibc ABI shims (harmless elsewhere; required on NixOS) ---------
# The player ships ClearScript V8 and a Boehm GC that misbehave against a
# non-stock glibc. Two well-worn fixes, applied only if present/needed:
#   1. v8-deepbind.so forces RTLD_DEEPBIND on dlopen so V8's bundled libstdc++
#      isn't interposed (else: "free(): invalid size" in std::locale).
#   2. Disable AVX-512 string ops — the GC over-reads past buffers with the
#      EVEX memcpy variants (must include AVX512VL or glibc still picks them).
[ -n "${DCL_V8_SHIM:-}" ] && export LD_PRELOAD="${DCL_V8_SHIM}${LD_PRELOAD:+:$LD_PRELOAD}"
export GLIBC_TUNABLES="${GLIBC_TUNABLES:-glibc.cpu.hwcaps=-AVX512,-AVX512VL}"

GFX="${DCL_GFX_FLAG:--force-vulkan}"   # -force-vulkan | -force-glcore (see docs/03)
dcl_log "launching native player: $BIN ($GFX)"
# $DCL_FHS_WRAP wraps the ELF on NixOS (see config.sh); empty/no-op elsewhere.
exec $DCL_FHS_WRAP "$BIN" \
    -screen-width "${DCL_SCREEN_W:-1280}" -screen-height "${DCL_SCREEN_H:-720}" \
    -screen-fullscreen 0 "$GFX" \
    -logFile "$DCL_RIG_LOG_DIR/player.log" \
    "$@" ${DCL_PLAYER_EXTRA_ARGS:-}

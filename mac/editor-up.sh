#!/usr/bin/env bash
# =============================================================================
# editor-up.sh — launch the REAL GUI Unity editor on macOS, pointed at the
# Explorer project, with a fresh logfile, keep-awake, stale-state cleanup, and a
# boot watchdog. Target: "mac" (the one host here that can present a window).
#
#   ./mac/editor-up.sh                     # launch (or reuse a running) editor
#   DCL_MAC_APP_ARGS=--skip-auth-screen ./mac/editor-up.sh   # auto-login cached id
#
# Then drive it with mac/get-in-world.sh (Play → in-world → screenshot) or the
# lib/mac-driver.sh primitives directly. See docs/07-mac.md.
# =============================================================================
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/../config.sh"
. "$HERE/../lib/common.sh"
. "$HERE/../lib/mac-driver.sh"

[ "$(uname -s)" = Darwin ] || dcl_die "mac/editor-up.sh is macOS-only (uname=$(uname -s))"

# Already up on THIS project? Reuse it — one editor per project is the contract.
existing="$(dcl_mac_editor_pid || true)"
if [ -n "$existing" ]; then
    dcl_log "editor already running (pid $existing) on $DCL_PROJECT_DIR — reusing"
    echo "$existing" > "$HOME/.dcl-rig/mac-editor.pid" 2>/dev/null || true
    exit 0
fi

dcl_mac_keepawake

# Clear crash/lock state that would otherwise wedge boot behind a modal you'd
# have to OCR-and-click ("Recover backup scene?", a stale lock). Everything here
# is under Temp/ + Library/ — GENERATED state, never tracked source, so this
# never dirties the checkout. (Same hazard the Linux launcher clears.)
rm -f  "$DCL_PROJECT_DIR/Temp/UnityLockfile" 2>/dev/null || true
rm -rf "$DCL_PROJECT_DIR/Temp/__Backupscenes" "$DCL_PROJECT_DIR/Library/CurrentScene" 2>/dev/null || true

# The Unity 6000.4 LifecycleController boot NRE bites the GUI editor on Mac too,
# not just headless Linux: observed here as "Lifecycle ERROR : Failed to setup
# LifecycleManagement ... NullReferenceException" during pre-deserialization,
# which leaves the explorer's world systems half-initialized so Play never
# reaches Completed. The fix renames the UNITY_INCLUDE_TESTS asmdef dirs in
# Library/PackageCache (generated state — never tracked source). ON by default,
# like the Linux launcher; set DCL_MAC_DISABLE_TEST_ASMDEFS=0 to skip it.
if [ "${DCL_MAC_DISABLE_TEST_ASMDEFS:-1}" = 1 ]; then
    "$HERE/../linux/disable-test-asmdefs.sh" "$DCL_PROJECT_DIR" || true
fi

# Drop the rig's editor harness into the project as copies (relative-path,
# gitignored, never symlinks). One source of truth ($DCL_RIG_REPO/unity), so every
# platform gets the identical harness without polluting the client repo. Set
# DCL_MAC_DEPLOY_HARNESS=0 to skip (e.g. when the project already vendors it).
if [ "${DCL_MAC_DEPLOY_HARNESS:-1}" = 1 ]; then
    dcl_harness_deploy "$DCL_PROJECT_DIR" || dcl_log "harness deploy skipped/failed (continuing)"
fi

UNITY="$(dcl_unity_bin)"
[ -x "$UNITY" ] || dcl_die "Unity not found at $UNITY (set DCL_UNITY_BIN)"
LOG="$DCL_MAC_LOG"; mkdir -p "$(dirname "$LOG")"

# Fresh log per launch (Unity overwrites -logFile on open). App args (-- prefixed)
# are read by the Explorer's own arg parser even when launched as the editor.
dcl_log "launching editor: $UNITY -projectPath $DCL_PROJECT_DIR (log=$LOG)"
"$UNITY" -projectPath "$DCL_PROJECT_DIR" -logFile "$LOG" \
    ${DCL_MAC_APP_ARGS:-} ${DCL_EDITOR_EXTRA_ARGS:-} &
editor_pid=$!

# Warmup watchdog: poll the WHOLE window (not "until first healthy") so an early
# crash or fatal compile error is actually caught. Success = process still alive
# at the end of the window with no fatal marker; a strong "ready" marker ends the
# wait early. A booted GUI editor is reliable here, so one retry is plenty.
warmup="${DCL_MAC_WARMUP:-180}"; waited=0; ok=0
while [ "$waited" -lt "$warmup" ]; do
    sleep 5; waited=$((waited + 5))
    if ! kill -0 "$editor_pid" 2>/dev/null; then
        dcl_log "editor process exited during warmup (see $LOG)"; ok=0; break
    fi
    if grep -qE 'Fatal error|Aborting batchmode' "$LOG" 2>/dev/null; then
        dcl_log "fatal marker in log during warmup"; ok=0; kill "$editor_pid" 2>/dev/null || true; break
    fi
    # "Initial Refresh End" = the editor finished its first asset import and is at
    # the main window (confirmed marker on 6000.4.0f1 here). "Completed reload" is
    # the post-compile fallback. Either means it's ready to drive.
    if grep -qE 'Application\.AssetDatabase Initial Refresh End|Completed reload, in [0-9]' "$LOG" 2>/dev/null; then
        ok=1; break
    fi
done

# If the warmup elapsed with the process still alive, treat that as up too — the
# "ready" markers vary across Unity patch releases; liveness is the backstop.
if [ "$ok" != 1 ] && kill -0 "$editor_pid" 2>/dev/null; then ok=1; fi

if [ "$ok" = 1 ]; then
    dcl_log "editor up (pid $editor_pid)"
    echo "$editor_pid" > "$HOME/.dcl-rig/mac-editor.pid" 2>/dev/null || true
    exit 0
fi
dcl_die "editor failed to boot (see $LOG)"

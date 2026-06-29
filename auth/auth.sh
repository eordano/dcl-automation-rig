#!/usr/bin/env bash
# shellcheck shell=bash
# =============================================================================
# auth/auth.sh — automated login with a throwaway wallet, no browser human, no
# desktop OpenURL. The web-stack port of the Unity rig's lib/auth.sh.
#
# The signing half is UNCHANGED (auth-driver.py, transport-agnostic): make/reuse
# a disposable wallet, fetch the v2/requests challenge from
# auth-api.decentraland.org, dcl_personal_sign it, POST the outcome.
#
# What changed is the CAPTURE half. The Unity rig shimmed `xdg-open` to log
# Application.OpenURL(<auth-request-url>) on a desktop. In the web stack there is no
# desktop OpenURL — the subjects are a browser and an in-browser engine:
#
#   1. BROWSER capture: the auth-request URL is IN-PAGE. Read it from the DOM /
#      console over CDP (bevy/cdp-capture.py) into $DCL_URL_LOG, then sign.
#      See browser-capture.md. (write-up; full new-wallet wiring unverified here)
#   2. ENGINE console capture: the engine's main-thread console commands
#      /login_guest, /login_previous, /logout ARE reachable today (generated onto
#      window.engine from console-command metadata — see docs/bridge-status.md).
#      The guest / previous-login path needs NO challenge signing at all; drive
#      it directly. New-wallet signing wiring is the unverified part.
#
# So there is NO xdg-open shim to install — the capture is a CDP read or an
# engine console call. dcl_auth_sign() is unchanged; dcl_auth_engine_login() is
# the new engine-side path.
# =============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$HERE/../config.sh" 2>/dev/null || true
. "$HERE/../lib/common.sh" 2>/dev/null || true

# --- The SIGNING half (unchanged) --------------------------------------------
# Run the signer. Blocks until it sees an auth URL in $DCL_URL_LOG (default
# 120s) and signs it. Pass a request-id to skip the watch and sign directly.
# Needs python modules eth_account + requests — fail with a clear message rather
# than a deep traceback (use a venv/pip or an FHS python that has them).
dcl_auth_sign() {
    python3 -c 'import eth_account, requests' 2>/dev/null || {
        echo "auth needs python modules: eth_account + requests" >&2
        echo "  pip install eth-account requests   (or run via an FHS python that has them)" >&2
        return 1
    }
    URL_LOG="$DCL_URL_LOG" WALLET_FILE="$DCL_WALLET_FILE" \
        python3 "$HERE/auth-driver.py" "$@"
}

# --- The CAPTURE half, browser variant ---------------------------------------
# Read the in-page auth-request URL from the running browser over CDP and append
# it to $DCL_URL_LOG (the shape auth-driver.py's watcher expects), then sign.
# Requires a chromium already up with CDP on $DCL_WEB_CDP_PORT (lib/chromium-launch.sh)
# sitting on the auth screen. The URL pattern is the same one auth-driver.py
# matches (decentraland.org/auth/requests/<uuid>); cdp-capture.py streams console
# + page text and exits on the first match, which we tee into the log.
#   dcl_auth_capture_browser [cdp_port]
dcl_auth_capture_browser() {
    local cdp="${1:-$DCL_WEB_CDP_PORT}"
    mkdir -p "$(dirname "$DCL_URL_LOG")"
    dcl_log "reading auth URL from browser console/DOM over CDP :$cdp -> $DCL_URL_LOG"
    # Stream until the auth-request URL appears, append the matching line to the
    # log auth-driver.py tails. CDP_PORT selects the endpoint; the sentinel is the
    # auth host so capture exits as soon as the engine surfaces the URL.
    CDP_PORT="$cdp" python3 "$HERE/../bevy/cdp-capture.py" \
        "decentraland.org/auth/requests/" 60 \
        | grep -Eo 'https://decentraland\.org/auth/requests/[a-f0-9-]{36}' \
        | head -1 | while IFS= read -r url; do
            printf '[%s] %s\n' "$(date -Iseconds)" "$url" >> "$DCL_URL_LOG"
            dcl_log "captured auth URL -> $DCL_URL_LOG"
        done
    dcl_auth_sign
}

# --- The CAPTURE half, engine console variant --------------------------------
# Drive the engine's MAIN-THREAD console-command login directly. These are the
# only auth ops reachable on the main thread today (window.engine.loginGuest /
# loginPrevious / logout, generated from console-command metadata). Guest /
# previous login need NO challenge signing — so this path is wireable now; the
# full new-wallet signing wiring (capture+sign) is the unverified part.
# Requires a chromium with CDP up on $DCL_WEB_CDP_PORT running the engine bundle.
#   dcl_auth_engine_login <guest|previous|logout> [cdp_port]
dcl_auth_engine_login() {
    local which="${1:-guest}" cdp="${2:-$DCL_WEB_CDP_PORT}"
    local cmd
    case "$which" in
        guest)    cmd="window.engine.loginGuest()" ;;
        previous) cmd="window.engine.loginPrevious()" ;;
        logout)   cmd="window.engine.logout()" ;;
        *) dcl_die "auth engine login: want guest|previous|logout, got '$which'" ;;
    esac
    dcl_log "engine console login: $cmd (CDP :$cdp)"
    # cdp-capture.py injects JS at load and streams the reply; we evaluate the
    # console-command wrapper and watch for its resolution. The login_* commands
    # exist (system_bridge/agent_commands.rs); this is the bucket-(1) path.
    CDP_PORT="$cdp" DCL_CDP_EVAL="$cmd" \
        python3 "$HERE/../bevy/cdp-capture.py" "login" 30
}

# Allow `auth.sh <subcommand>` direct use, or sourcing for the functions.
if [ "${BASH_SOURCE[0]}" = "${0:-}" ]; then
    case "${1:-}" in
        sign)          shift; dcl_auth_sign "$@" ;;
        browser)       shift; dcl_auth_capture_browser "$@" ;;
        engine)        shift; dcl_auth_engine_login "$@" ;;
        *) echo "usage: auth.sh {sign [id] | browser [cdp] | engine <guest|previous|logout> [cdp]}" >&2; exit 2 ;;
    esac
fi

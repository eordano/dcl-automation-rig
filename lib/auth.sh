# shellcheck shell=bash
# =============================================================================
# lib/auth.sh — automated login with a throwaway wallet, no browser, no human.
#
# The problem: DCL clients (editor and player alike) authenticate by calling
# Application.OpenURL(<auth-request-url>) and waiting for the wallet to sign the
# challenge out-of-band. Headless, nobody is there to click.
#
# The trick, in two halves:
#   1. CAPTURE the URL. We shadow `xdg-open` with auth/xdg-open, which appends
#      every OpenURL() target to $DCL_URL_LOG and then forwards to the real one.
#      - Native editor:  put auth/ first on PATH.
#      - Wine player:    also point the WineBrowser registry key at the shim
#        (see linux/binary-proton.sh) — Wine bypasses PATH for the browser.
#   2. SIGN it. auth/auth-driver.py tails $DCL_URL_LOG, pulls the request id,
#      fetches the challenge from auth-api.decentraland.org, signs it with the
#      disposable wallet, and POSTs the signature back. Login completes.
# =============================================================================

# Install the URL-capture shim onto PATH for editor-style (PATH-respecting) apps.
# Returns the export the caller should apply. Usage:  eval "$(dcl_auth_shim_env)"
dcl_auth_shim_env() {
    mkdir -p "$(dirname "$DCL_URL_LOG")"
    printf 'export PATH=%q:$PATH; export URL_LOG=%q\n' "$DCL_RIG_REPO/auth" "$DCL_URL_LOG"
}

# Run the signer. Blocks until it sees an auth URL (default 120s) and signs it.
# Pass a request-id to skip the watch and sign that request directly.
# Needs the python modules eth_account + requests — fail with a clear message
# rather than a deep traceback if they're absent (use a venv/pip or an FHS python).
dcl_auth_sign() {
    python3 -c 'import eth_account, requests' 2>/dev/null || {
        echo "auth needs python modules: eth_account + requests" >&2
        echo "  pip install eth-account requests   (or run via an FHS python that has them)" >&2
        return 1
    }
    URL_LOG="$DCL_URL_LOG" WALLET_FILE="$DCL_WALLET_FILE" \
        python3 "$DCL_RIG_REPO/auth/auth-driver.py" "$@"
}

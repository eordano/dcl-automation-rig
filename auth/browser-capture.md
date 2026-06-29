# Throwaway-auth — the capture half, rewritten for the browser/engine

The web-stack port of the rig's **throwaway-auth** capability. The capability has
two halves; only the capture half changed.

## What ports unchanged: the SIGNING half (`auth-driver.py`)

`auth-driver.py` is **transport-agnostic** and ports verbatim:

1. make-or-reuse a disposable eth wallet (`Account.create()`, persisted to
   `$DCL_WALLET_FILE`),
2. fetch the challenge from `auth-api.decentraland.org/v2/requests/<id>`,
3. `dcl_personal_sign` it with the wallet,
4. POST the outcome to `.../v2/requests/<id>/outcome`.

It doesn't care WHO captured the request id — a Unity `OpenURL`, a browser DOM
read, or an engine console command. That is why the signer is reused as-is.

## What changed: the CAPTURE half (no `xdg-open` shim)

The Unity rig captured the auth-request URL by **shadowing `xdg-open`**: every
`Application.OpenURL(<auth-request-url>)` the desktop client made was logged to
`$DCL_URL_LOG` before forwarding to the real opener. In the web stack there is **no
desktop OpenURL** — the subject is a browser (and an in-browser engine), so the
URL never leaves the page through a desktop opener. Two replacements:

### 1. Browser DOM/console capture (CDP read)

When the wasm engine (or the React HUD) initiates login, the auth-request URL is
**in the page** — rendered as a link / QR and logged to the JS console. Read it
over CDP instead of from a desktop shim:

```bash
auth/auth.sh browser            # CDP-read the URL into $DCL_URL_LOG, then sign
```

`dcl_auth_capture_browser` runs `bevy/cdp-capture.py` against the live chromium
(CDP on `$DCL_WEB_CDP_PORT`, brought up by `lib/chromium-launch.sh`), streams the
console + page text, and extracts the first
`https://decentraland.org/auth/requests/<uuid>` — the **same** pattern
`auth-driver.py` matches. It appends that to `$DCL_URL_LOG`, then calls
`dcl_auth_sign`, which signs and POSTs exactly as before.

> Status: write-up + wiring. The DOM/console read and the signer are each
> exercised in isolation; the full new-wallet capture→sign round-trip through a
> real WebGPU engine boot is **unverified here** and labelled so (it needs a real
> engine boot, the same wall every "wired" claim in this rig carries — see
> [`../docs/bridge-status.md`](../docs/bridge-status.md)).

### 2. Engine console login (main-thread, reachable TODAY)

The engine exposes three login console commands on the **main thread** today —
`/login_guest`, `/login_previous`, `/logout` (defined in
`bevy-explorer/crates/system_bridge/src/agent_commands.rs`, surfaced as
`window.engine.loginGuest()` / `loginPrevious()` / `logout()` by the
console-command-metadata generator in `engine.js`). They are bucket-(1)
main-thread-reachable, unlike the worker-scoped identity stream.

```bash
auth/auth.sh engine guest       # window.engine.loginGuest() over CDP
auth/auth.sh engine previous    # window.engine.loginPrevious()
auth/auth.sh engine logout      # window.engine.logout()
```

- **Guest / previous login** need NO challenge signing — they're a direct
  console call, so this path is **wireable now**.
- **New-wallet login** (a fresh disposable wallet that must sign the v2/requests
  challenge) still needs the capture→sign round-trip of variant 1; that wiring is
  the **unverified** part.

## How they fit together

```
              ┌─ variant 1: CDP read in-page URL ─┐
login starts ─┤                                   ├─> $DCL_URL_LOG ─> auth-driver.py (sign+POST)
              └─ variant 2: engine console login ─┘   (guest/prev: no sign needed)
```

Both replace the `xdg-open` shim; the signer downstream is identical to the
Unity rig's. The honest boundary: **guest/previous login is reachable today;
full new-wallet signing through a real engine boot is unverified here.**

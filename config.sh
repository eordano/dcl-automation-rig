# shellcheck shell=bash
# =============================================================================
# config.sh — single source of truth for the web-stack automation rig.
#
# This is the web-stack PORT of the Unity rig's config.sh. The Unity rig drove a
# Unity 6 Editor / IL2CPP player; this rig drives TWO web-stack subjects instead:
#   - bevy-explorer  — the engine, native (decentra-bevy) AND its wasm build
#     running in headless chromium.
#   - sites/app + catalyst — the React HUD overlay (bevy-overlay.* routes)
#     served by catalyst, walked read-only in a browser.
#
# Every shell script in rig/ sources this file. It carries the facts TRUE FOR
# EVERY capability (checkout roots, ports, the headless-display rig id, the
# catalyst upstream) so nothing below repeats them.
#
# Everything is overridable from the environment — set a var before sourcing
# (or in your shell rc) to point the rig at your own checkout/host. No
# machine-specific path is baked in as the only option.
# =============================================================================

# --- The web-stack checkout roots ----------------------------------------------
# Resolved relative to THIS file so a clone anywhere just works; override any to
# point at a sibling checkout. DCL_STACK_REPO is the parent dir that holds the
# sibling client/server checkouts (bevy-explorer, sites, catalyst).
DCL_RIG_REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
: "${DCL_STACK_REPO:=$(cd "$DCL_RIG_REPO/.." && pwd)}"
export DCL_RIG_REPO DCL_STACK_REPO

# bevy-explorer — the engine. Native binary + the wasm `deploy/web` bundle both
# live under here. (WHY a var, not a fixed path: a perf A/B builds two side
# checkouts and points DCL_BEVY_REPO at each.)
: "${DCL_BEVY_REPO:=$DCL_STACK_REPO/bevy-explorer}"
: "${DCL_BEVY_BIN:=$DCL_BEVY_REPO/target/release/decentra-bevy}"   # native engine binary
: "${DCL_BEVY_WEB_DIR:=$DCL_BEVY_REPO/deploy/web}"                 # the served wasm bundle root
export DCL_BEVY_REPO DCL_BEVY_BIN DCL_BEVY_WEB_DIR

# sites/app — the React HUD overlay (bevy-overlay.* routes) + catalyst backend.
: "${DCL_SITES_REPO:=$DCL_STACK_REPO/sites}"
: "${DCL_CATALYST_REPO:=$DCL_STACK_REPO/catalyst}"
export DCL_SITES_REPO DCL_CATALYST_REPO

# The dcl-shell / FHS wrapper the bevy build + native binary run under. On NixOS
# the glibc ELFs need an FHS provider; auto-detected (empty = no-op on a normal
# distro). Override DCL_SHELL to your build shell.
: "${DCL_SHELL:=dcl-shell}"
export DCL_SHELL

# --- The web measurement subjects --------------------------------------------
# The wasm bundle is served with COOP/COEP (SharedArrayBuffer); chromium drives
# it over CDP. These name the served dir + ports the web capabilities share.
: "${DCL_WEB_PORT:=8080}"                                # COEP static server for the wasm bundle
: "${DCL_WEB_CDP_PORT:=9344}"                            # chromium remote-debugging port
# The local CI realm (scene-explorer-tests) the product tour + web A/B play against.
: "${DCL_CI_REALM_PORT:=5199}"
: "${DCL_CI_REALM_PATH:=scene-explorer-tests}"
export DCL_WEB_PORT DCL_WEB_CDP_PORT DCL_CI_REALM_PORT DCL_CI_REALM_PATH

# >>> The single load-bearing gate (see docs/bridge-status.md + BUILD-WASM-BENCHMARK.md) <<<
# A STOCK wasm bundle never emits WASMBENCH_RESULT (it's option_env!-gated in
# web.rs on DCL_WASM_BENCHMARK). Without that build, web measurement is
# submitFps + functional-only — orbit_cpu and per-scene tick counts are absent.
# This flag is informational here; it documents what a bundle must be built with.
: "${DCL_WASM_BENCHMARK:=0}"
export DCL_WASM_BENCHMARK

# --- Catalyst content upstream (for the CORS/CORP proxy) ---------------------
# The COEP-isolated wasm bundle can only load cross-origin content that answers
# with ACAO + Cross-Origin-Resource-Policy: cross-origin. $DCL_CATALYST_UPSTREAM
# is the SAME upstream sites/app uses (sites/app/lib/catalyst/client.ts:20
# DEFAULT_BASE). Point it at a LOCAL catalyst core for deterministic loads (or a
# public catalyst); the proxy adds the missing headers in front of it. No default
# is baked in — set it to your catalyst host. See bevy/catalyst-cors-proxy.py.
: "${DCL_CATALYST_UPSTREAM:=}"
: "${DCL_CATALYST_PROXY_PORT:=5142}"
export DCL_CATALYST_UPSTREAM DCL_CATALYST_PROXY_PORT

# The catalyst-served origin the React-HUD atlas URL-walk navigates. By default
# the local sites dev server; override at the deployed origin to walk that.
: "${DCL_HUD_BASE:=http://127.0.0.1:3000}"
export DCL_HUD_BASE

# --- Headless display / multi-rig (Linux only) -------------------------------
# A "rig" is identified by its wayvnc port. Run several in parallel on one host,
# one per port. All per-rig state is scoped under these dirs. See lib/headless-display.sh.
#
# >>> CRITICAL DIFFERENCE FROM THE UNITY RIG: GPU gles2, NOT pixman. <<<
# The Unity rig defaulted WLR_RENDERER=pixman (pure CPU, runs anywhere). Real
# WebGPU presentation + non-black capture + frame timing need a GPU-backed sway
# with DRI3 (see lib/README.md + docs/08). So this rig defaults to gles2.
: "${DCL_RIG_PORT:=5913}"                                # wayvnc port == rig id
: "${DCL_VNC_BIND:=localhost}"                           # bind addr for wayvnc (loopback)
: "${DCL_WLR_RENDERER:=gles2}"                           # GPU; was pixman in the Unity rig
: "${DCL_RIG_RT:=${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/dcl-rig-$DCL_RIG_PORT}"
: "${DCL_RIG_TMP:=/tmp/dcl-rig-$DCL_RIG_PORT}"
: "${DCL_RIG_LOG_DIR:=$DCL_RIG_RT/logs}"
export DCL_RIG_PORT DCL_VNC_BIND DCL_WLR_RENDERER DCL_RIG_RT DCL_RIG_TMP DCL_RIG_LOG_DIR

# --- Auth (throwaway wallet) -------------------------------------------------
# The rig signs DCL's auth challenge with a disposable wallet. See auth/.
# The CAPTURE half is rewritten for the browser/engine console (no xdg-open shim).
: "${DCL_WALLET_FILE:=$HOME/.dcl-rig/throwaway-wallet.json}"
: "${DCL_URL_LOG:=$HOME/.dcl-rig/urls.log}"              # where the CDP auth-URL reader logs
export DCL_WALLET_FILE DCL_URL_LOG

# --- PR-review loop ----------------------------------------------------------
# Reviews upstream PRs by rebasing + stacking the rig's fix branch onto each one,
# compiling (cargo + tsc + cargo) and capturing (atlas URL-walk + web tour), and
# writing a review. See pr-review/ and docs/00-coverage-matrix.md (pr-review-loop).
# Retargeted off decentraland/unity-explorer onto the web-stack repos.
: "${DCL_REVIEW_REPO_SLUG:=decentraland/bevy-explorer}" # GitHub owner/repo whose open PRs to review
: "${DCL_REVIEW_OUT:=$HOME/dcl-pr-review}"          # where reviews + per-PR PNGs land
: "${DCL_REVIEW_FIX_BRANCH:=auto-fixes}"                 # local branch holding the fixes to stack
: "${DCL_REVIEW_STACK_BASE:=our-stack-base}"             # ref the fixes sit on ($BASE..$FIX = commits to cherry-pick)
export DCL_REVIEW_REPO_SLUG DCL_REVIEW_OUT DCL_REVIEW_FIX_BRANCH DCL_REVIEW_STACK_BASE

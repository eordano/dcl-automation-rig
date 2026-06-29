# rig/atlas — the UI-capture atlas, retargeted at the React HUD

The web-stack port of the rig's **ui-capture-atlas** capability — and the
**strongest adapt** in the rig. The Unity atlas solved a hard problem by
reflection: it had to *force* the Unity client into each UI state (drive the
auth FSM, open the right panel) before it could screenshot it. In the web stack that
hard part disappears: **every bevy-overlay screen is a URL-addressable route**
(`/client?panel=...`) served by catalyst, so forcing UI state is just a chromium
navigation.

## Atlas-by-construction (the thesis)

- Each `bevy-overlay.*` surface is a **URL** (`routes.json` is the table). The
  HUD keeps panels URL-addressable via `?panel=`/`?tab=`/`?address=` precisely so
  each journey step is a real link — see `sites/app/components/bevy-overlay/HudOverlay.tsx`.
- So the atlas is **wired-now**: the routes render REAL catalyst data (SSR) with
  honest per-source fixture fallback (each route's `source` is named in
  `routes.json`). Walking them produces named screenshots of real screens today.
- These are **DOM overlays** — `grim` captures them fine, unlike the WebGPU
  `<canvas>` (black here; see [`../docs/bridge-status.md`](../docs/bridge-status.md)).

## What we KEEP vs REPLACE

| Piece | Verdict | What |
|---|---|---|
| route→CODE registry | **KEEP** (ported to web-stack routes) | `atlas-codes.json` — the naming layer; `<CODE>-<route>.png`. |
| consolidate (keep-best / subset / back-up) | **KEEP / PORTED** | `consolidate-atlas.py` — registry-driven, route-source agnostic. |
| gen-index / digest | **KEEP / PORTED** | `gen-index.py` — INDEX.md (registry) + DIGEST.md (captured set). |
| hardness tiers | **RE-CAST** | from *reflection difficulty* to **data-availability** (anon vs auth variants — see `auth-capture.md`). |
| auth-capture pass | **RETARGETED** | `auth-capture.md` — anon vs authenticated route variants; ties to `../auth`. |
| recipes.json (Unity auth-FSM reflection) | **REPLACED** | `routes.json` — a URL/route table (path + query params + auth + source). |
| RunAtlasHeadless (Unity reflection driver) | **REPLACED** | `url-walk.sh` — a CDP URL-walker over catalyst-served routes. |

## Files

- `atlas-codes.json` — route→CODE registry (areas P/H/E/S/M/O), ported to the
  real web-stack routes (passport, notifications, community create/join,
  friend-request, map-jump, voice, backpack equip/emotes, outfit-save, settings
  + the resting HUD `?panel=` states).
- `routes.json` — the URL/route table (replaces recipes.json).
- `url-walk.sh` — drive headless chromium (via `../bevy/cdp-capture.py`) to each
  route, settle, screenshot, name from `atlas-codes.json`.
- `consolidate-atlas.py` / `gen-index.py` — naming + index/digest (ported).
- `auth-capture.md` — the anon/auth variant pass.
- `INDEX.md` — generated registry index.

## Run

```bash
. ../config.sh; . ../lib/common.sh; . ../lib/headless-display.sh
dcl_headless_up                       # rig sway (DOM capture needs the compositor)
DCL_HUD_BASE=http://127.0.0.1:3000 ./url-walk.sh ~/dcl/atlas/walk
# -> ~/dcl/atlas/walk/<CODE>-<route>.png + INDEX.md/DIGEST.md
```

The honest boundary (carried into every claim): **anon read surfaces are
wired-now**; **guest login is reachable now**; **identity-bearing variants
(name/wallet/friends/mic) are GATED on the worker-gap.** The atlas never claims a
logged-in stream the engine can't yet surface on the main thread.

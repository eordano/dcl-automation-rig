# Auth-capture pass — anonymous vs authenticated route variants

The web-stack retarget of the Unity atlas's **auth-capture pre-world pass**.

## What the Unity pass did

The Unity atlas had a pre-world pass that captured the auth/login/loading
surfaces (the `P*` codes) BEFORE the client reached interactive — because those
screens only exist while NOT logged in, and the reflection driver had to catch
them on the way through the auth FSM.

## What it becomes here

Because every bevy-overlay screen is a URL, there is no FSM to catch — but the
SAME distinction survives as a **data-availability** split, which is exactly the
re-cast of the Unity atlas's "hardness tiers" (see README): a route renders one
way with no identity and another with a guest/identity.

`routes.json` tags each route `auth: "anon"` or `auth: "auth"`:

- **anon** — the resting / static state. The `window.dclBridge` is null, so each
  HUD widget degrades to its ui3 static/empty rows. These capture WITHOUT any
  login (`DCL_ATLAS_AUTH=anon ./url-walk.sh`). Most read surfaces (explore, map,
  settings, communities list, passport-by-address) render real catalyst data
  here via SSR, with per-source fixture fallback — so they are **wired-now**.
- **auth** — the variant that needs an identity (friends, backpack, voice,
  notifications, community create/join, camera-reel, credits). The display name /
  wallet / friends connectivity come from the engine's identity + friends streams,
  which are **GATED on the worker-gap** (worker-scoped SystemApi, no main-thread
  op — see [`../docs/bridge-status.md`](../docs/bridge-status.md)). What IS
  reachable today is **guest login** via the engine console `/login_guest`
  (`window.engine.loginGuest()`), wired through [`../auth`](../auth). So the auth
  pass can establish a guest session now; the full identity-bearing variant is
  GATED.

## Running the two passes

```bash
DCL_ATLAS_AUTH=anon  ./url-walk.sh ~/dcl/atlas/anon     # no login; static/real-read state
# establish a guest session first (engine path), then:
../auth/auth.sh engine guest
DCL_ATLAS_AUTH=auth  ./url-walk.sh ~/dcl/atlas/auth     # authenticated variants
```

Both produce `<CODE>-<route>.png` via `consolidate-atlas.py`; keep-best means a
later auth capture of a route overwrites an earlier anon one if you want the
richer variant in the canonical atlas (or point them at separate out dirs to keep
both, as above).

## The honest line

- **anon read surfaces** = wired-now (real catalyst data, fixture fallback).
- **guest session** = reachable now (engine `/login_guest`).
- **identity-bearing auth variants** (name/wallet/friends/mic) = GATED on the
  worker-gap, exactly as the bridge ledger says — the atlas never implies it
  captured a logged-in friends list that the engine cannot yet surface on the
  main thread.

# Bridge status — the honest per-surface ledger (the rig's spine)

This is the single load-bearing reality every rig claim is measured against. It
inventories, per HUD surface, exactly which of THREE buckets it sits in, so the
rig never claims more than scene-read-now + the three console-command actions.

## The architecture (what talks to what)

- **bevy-explorer** is an in-browser **wasm** build (the engine + scene Web
  Workers). It mounts the React HUD over its `<canvas id="bevy-canvas">`.
- The **React HUD** (`sites/app/components/bevy-overlay/*`) is a **sibling DOM
  overlay** on the **main thread**. It talks to the engine via a
  `window.dclBridge` shim mirroring `crates/system_bridge` `SystemApi`
  (`sites/app/components/bevy-overlay/bridge.ts`). In THIS app the engine is never
  bundled, so `getBridge()` is essentially always null and each widget degrades
  to its ui3 static/empty state.

## The single unsolved problem: the worker↔main-thread gap

Every `SystemApi` op wrapper takes `&WorkerContext` (the scene Web Worker), while
the HUD + `window.dclBridge` live on the **main thread**. The ONLY main-thread
seam is `window.engine.<cmd>()`, **generated** (`engine.js:303-351`) from
console-command metadata. Today exactly these console commands reach
SystemApi/engine on the main thread:

- `system_bridge/agent_commands.rs`: `/chat`→`SendChat`, `/live_scenes`→
  `LiveSceneInfo`, `/login_guest`, `/login_previous`, `/logout`.
- `restricted_actions/agent_commands.rs`: `/move_player_to`, `/walk_player_to`,
  `/emote`.

Streams (`GetChatStream`, `GetMicState`, friends connectivity, scene-loading)
have **NO console command and NO main-thread path**.

## The three buckets (govern every claim)

### (1) WIRED-TO-REAL-DATA-NOW

- **scene-read** via `/live_scenes` — **title only**; coords/realm are LOST
  through the human-readable console string (`live_scenes_cmd` formats
  `"<title> [<hash>...]"`). The Minimap folds the title; coords/realm don't survive.
- **three actions** with a console command and a verified call shape:
  **chat send** (`/chat`→`SendChat`), **move/teleport** (`/move_player_to`),
  **emote** (`/emote`).

> Even "wired" still requires a **real WebGPU engine boot** to demonstrate — and
> the ONLY executed verification, `verify-hud.mjs`, does NOT boot the engine: it
> injects a **fake** `window.engine.liveScenes` (`verify-hud.mjs:168-175`) that
> returns the genuine `/live_scenes` reply STRING. That validates the transport +
> parse + BridgeState push + overlay fold using the real reply format, but it is
> not a real boot. So: never claim "works/wired" without a real WebGPU engine boot.

### (2) WIREABLE-BUT-GATED-ON-THE-WORKER-GAP

Real SystemApi reads/streams exist but are **worker-scoped**; they need a NEW
main-thread `#[wasm_bindgen]` op or a postMessage relay (a wasm rebuild) before
the main-thread HUD can reach them:

- **identity** (`GetParams` → name/wallet/guest),
- **chat-stream** (`GetChatStream`),
- **friends** (`GetFriends`/`GetOnlineFriends`/connectivity),
- **mic** (`GetMicState`, `SetMicEnabled`).

### (3) NO-ENGINE-PATH-EXISTS-YET

Nothing the HUD needs is truly path-less at the engine **except `OpenExplore`**,
which is intentionally **client-only** (a pure panel toggle — no engine call).

## Correction baked into the matrix (don't mis-bucket these)

**`PlayEmote` and `Teleport` have NO `SystemApi` ENUM variant**, but they ARE
**main-thread-reachable** through `/emote` and `/move_player_to`. So they are
**bucket (1) action-capable today**, NOT bucket (3). The absence of an enum
variant is not the absence of a main-thread path.

## Per-surface ledger (quick reference)

| HUD surface | Bucket | Why |
|---|---|---|
| Minimap scene title | (1) wired-now | `/live_scenes` (title only; coords/realm lost) |
| Send chat | (1) action | `/chat`→`SendChat` |
| Move / teleport | (1) action | `/move_player_to` (no enum variant, but reachable) |
| Play emote | (1) action | `/emote` (no enum variant, but reachable) |
| Guest / previous login, logout | (1) action | `/login_guest` `/login_previous` `/logout` |
| Identity (name/wallet) | (2) GATED | `GetParams` worker-scoped; needs a main-thread op |
| Chat stream | (2) GATED | `GetChatStream` worker-scoped |
| Friends | (2) GATED | friends connectivity worker-scoped |
| Mic state / set mic | (2) GATED | `GetMicState`/`SetMicEnabled` worker-scoped |
| Open Explore panel | (3) client-only | pure panel toggle; no engine call by design |

## Invariants the rig repeats everywhere

1. Never claim "works"/"wired" without a **real WebGPU engine boot**.
2. The **worker-gap blocks all streams** (chat/friends/mic/identity).
3. The only executed verification used an **injected fake** (`verify-hud.mjs`).
4. The rig drives bevy-explorer's and catalyst's own primitives **READ-ONLY** and
   never modifies the vendored upstreams.

See the 100-pager (the full bevy-on-react bridge integration writeup) for the
exhaustive per-op trace; this ledger is its summary, and it is what keeps every
capability verdict inside the three buckets.

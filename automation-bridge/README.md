# Automation bridge — license-free, in-built-player UI driving over DCL's own CDP bridge

Drive a **built PR player** (find/click/component/exec/hierarchy/text) over a WebSocket, with
**no AltTester SDK, no AltTester Desktop relay, no per-seat license, no MetaForge gateway**. It
reuses Decentraland's own `chrome-devtool-protocol-unity` bridge (already compiled into the player)
plus the reflection command engine factored out of `ClaudeIPC`.

```
driver (Python / Claude / rig CDP harness)
        │  ws://127.0.0.1:1474   {"id":N,"method":"Automation.click","params":{"text":"JUMP INTO DECENTRALAND"}}
        ▼
   Bridge  (decentraland/chrome-devtool-protocol-unity, +2 tiny variants)
        │  handleMethod  (BACKGROUND thread)
        ▼
   AutomationBridgeHandler ──Enqueue──▶ MainThreadPump (Update) ──▶ AutomationCore.Dispatch  (reflection)
        ▲                                                                     │
        └──────────────── {"id":N,"result":{...}} ◀───────────────────────────┘
```

## Files
| File | Repo | Role |
|---|---|---|
| `chrome-devtool-protocol-unity/CDPRequest.cs` | `decentraland/chrome-devtool-protocol-unity` | **+`CDPMethod.Custom`** variant (carries method **and** params); `FromRaw` default → `Custom` |
| `chrome-devtool-protocol-unity/CDPResponse.cs` | same | **+`CDPResult.Json`** variant (raw JSON passthrough); `ToJson` rewritten with `Is*` accessors |
| `unity-explorer/AutomationCore.cs` | `decentraland/unity-explorer` | player-safe reflection engine (UITK + uGUI), `Dispatch(op,args)→json`. No `UnityEditor` dep |
| `unity-explorer/AutomationInput.cs` | same | raw input injection (click-at/key/type/swipe) via the new Input System (`QueueStateEvent`) |
| `unity-explorer/AutomationScreenshot.cs` | same | composited base64-PNG screenshot (`WaitForEndOfFrame` coroutine; async path) |
| `unity-explorer/AutomationBridgeHandler.cs` | same | mounts the bridge on :1474, marshals to main thread, dispatches `Automation.*` |
| `unity-explorer/MainThreadPump.cs` | same | `DontDestroyOnLoad` MonoBehaviour pumping queued main-thread work in `Update` |
| `unity-explorer/NoopBrowser.cs` | same | `IBrowser` that does nothing (don't launch Chrome; IL2CPP-safe) |
| `unity-explorer/WIRING.md` | same | the insertions (build define, flag, boot, optional editor toggle) |

## Why this is the whole job
- The **WebSocket server already exists** in the player (`Bridge`, port-bound, `handleMethod` hook,
  `BridgeStatus.HasListeners` gating). We add **two REnum variants** to make it a generic RPC server.
- The **command implementations already exist** in `ClaudeIPC` (we just factor the player-safe ones
  into `AutomationCore`). The editor `ClaudeIPC` and this player handler share one engine.
- It's **compiled out of release entirely** (`#if !UNITY_WEBGL && DCL_AUTOMATION_BRIDGE`, like AltTester's `#if ALTTESTER`), then gated at runtime behind `LAUNCH_AUTOMATION_BRIDGE`, and `exec` is opt-in (`DCL_AUTOMATION_ALLOW_EXEC=1`). Release builds don't contain it at all.

## Apply order
1. Patch the package (2 files) in a worktree of `chrome-devtool-protocol-unity`.
2. Add the 6 `.cs` files + wiring to a `unity-explorer` worktree.
3. Build a player with `-launch-automation-bridge`; connect a WS client to `:1474`.

## Threading (important)
`Bridge` invokes `handleMethod` on a **background thread** (`Bridge.cs:20`). Unity API is main-thread-only,
so `AutomationBridgeHandler` enqueues onto `MainThreadPump` (a `DontDestroyOnLoad` MonoBehaviour pumped in
`Update`) and blocks the bridge thread on a `TaskCompletionSource` (10s timeout) for the result (a late
main-thread completion after a timeout is a harmless no-op, never a `Set()` on a disposed primitive).

## Known limits (be honest)
- **WebGL**: a page can't host a WS server → bridge is `#if !UNITY_WEBGL` (AltTester has the same gap).
- **Input**: UITK `Clickable.clicked` / uGUI `onClick` invocation **plus** raw coordinate/key injection
  (click-at/key/type/swipe via the Input System's `QueueStateEvent`) are built — see `AutomationInput.cs`.
  Caveat: `type` drives key *state*, not the text-input stream, so a focused UITK `TextField` is set directly.
- **Screenshot**: implemented as an async op (`AutomationScreenshot.cs`, `WaitForEndOfFrame` coroutine),
  served off the handler's dedicated screenshot path (not the synchronous `Dispatch`).
- **Not AltDriver-wire-compatible**: existing `explorer-automation` AltTester tests still won't connect
  unchanged — that's the separate "speak AltTester's protocol" shim. This is for Claude/Python/rig driving.

## Verify
Both repos are imported on the Mac host (`unity-explorer-9064` depends on the CDP package), so this can be
compile-verified there the same way the harness was: patch the package + drop the 6 files in, batch-compile,
and (optionally) build a player and poke `:1474` with a WS client. The one thing to confirm on first compile
is REnum's generated `IsCustom`/`IsJson`/`FromCustom`/`FromJson` accessors (used by the patches).

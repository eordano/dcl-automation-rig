# rig/loop — the autonomous QA loop, session swapped to bevy-on-react

The web-stack port of the rig's **autonomous-loop** capability. The scaffolding is
ported intact; only the inner session target changed.

## What ports (the scaffolding, intact)

- **lock + TTL** — one tick at a time; a stuck tick's lock auto-expires.
- **state-carry** — `dcl-loop-state.md` is read at the top of every tick and
  updated at the bottom (long-running memory across ticks).
- **pause** — a `LOOP PAUSED` banner in the state file makes a tick a no-op.
- **preflight** — run only on a ready stack; never auto-start it.
- **regression-sentinel gate** — grep the session report against
  `regression-checks.txt`; any hit fails the tick loudly.
- **fix discipline** — the loop NEVER edits/commits here. Choosing + applying a
  fix is the operator/agent's call, under the guardrails: pick the
  highest-value *verifiable* fix, fix + verify, commit PR-sized to the
  `auto-fixes` branch, **never `git add -A`, NEVER push.**
- **retention** — keep the last N run-logs + session reports.

## What swapped (the session)

| | Unity rig | web-stack |
|---|---|---|
| inner session | `vm/run-playtest.sh` (Unity in a Windows VM) | `loop/session.sh` |
| measured baseline | VM atlas/playtest harness report | web product tour + React-HUD URL-walk + catalyst health |
| preflight | "is the QEMU VM + guest SSH up?" | "is the catalyst HUD origin reachable?" |
| sentinels | C#/Unity NRE strings | wasm hard-failures + broken routes + catalyst non-2xx |

`session.sh` runs the three measured steps and writes one report the loop greps —
exactly the shape the Unity report had.

## Files

- `run-loop.sh` — one tick (PORTED scaffolding; swapped session call + preflight).
- `session.sh` — the bevy-on-react measured baseline (REPLACES `vm/run-playtest.sh`).
- `dcl-loop-state.template.md` — the memory-carry state file (PORTED, retargeted).
- `regression-checks.txt` — sentinels for the bevy-on-react session (PORTED + retargeted).

## Honest note

The production loop is **paused / unverified even for Unity** — this is the
scaffolding plus a swapped session, not a proven turnkey loop. The session steps
themselves carry their own honesty: the web tour is functional-only (per-scene
ticks GATED on the WASMBENCH build), and identity/streams are GATED on the
worker-gap (see [`../docs/bridge-status.md`](../docs/bridge-status.md)). A tick
that only leaves known-GATED items unverified is `GATED-ONLY`, **not** a
regression.

## Run

```bash
cp dcl-loop-state.template.md dcl-loop-state.md     # once, before first run
DCL_HUD_BASE=http://127.0.0.1:3000 ./run-loop.sh    # one tick (cron / /loop / timer)
```

# web-stack loop — current state (maintained by the loop; read this FIRST each tick)

<!--
This is the loop's long-running memory. The orchestrator reads it at the top of
every tick to decide what to do; the operator (you, or an agent) updates it at
the bottom of every tick. Keep newest first. Copy this template to
loop/dcl-loop-state.md (gitignored) before first run.

PAUSE: to pause the loop, add a "## ⏸️ LOOP PAUSED ..." banner right below this
comment. While present, run-loop.sh writes a "paused" note and exits without
running a session. Delete the banner to resume.

PORTED from the Unity rig's template; the baseline/sentinel vocabulary is
retargeted at the bevy-on-react session (web tour + HUD URL-walk + catalyst).
-->

Updated: <UTC> (<outcome>)
Prior:   <UTC> (<one-line summary, reverse chronological>)

## Baseline (what a healthy tick looks like)
- web product tour: all feature scenes ran (functional), 0 hard failures (no
  panic / wgpu-validation); closure_recursive/crash_overlay are known non-fatal
- React-HUD URL-walk: ≈ <N> named screens captured (anon read surfaces wired)
- catalyst health: / and /client answer 2xx
- known-GATED (NOT a regression): per-scene tick counts (WASMBENCH build absent),
  identity/chat-stream/friends/mic (worker-gap)

## Adopted fixes (sentinels — must stay at 0; see loop/regression-checks.txt)
- <bug/PR #> — <string it produced> — verified sessions <UTC>, <UTC>

## Fix queue (priority order — highest-value verifiable first)
1. <empty, or: cargo/tsc compile errors (goal 0) → wasm hard failures (panic/wgpu)
   → broken HUD routes → web perf (submitFps) → catalyst latency>

## Outcome flags (use one in the "Updated" line)
# CLEAN N/N — all checks passed, no fix needed
# INVALID attempt-X — session never ran end-to-end (environmental)
# ENV-LIMITED — environmental conditions blocked a step (HUD down, GPU absent)
# GATED-ONLY — only known-GATED items unverified (WASMBENCH/worker-gap); not a regression
# FIX <desc> — a verified fix was committed to auto-fixes
# USER <task> — operator-directed scope change

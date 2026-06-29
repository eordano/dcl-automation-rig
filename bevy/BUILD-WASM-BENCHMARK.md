# Building a DCL_WASM_BENCHMARK bundle — the prerequisite for web measurement

This is the single gating note behind every "GATED" verdict in the web half of
this rig. Read it before concluding that web A/B "doesn't work."

## The gate, stated once

The deterministic web metrics — **`orbit_cpu`** (per-frame CPU ms over a fixed
camera orbit) and **per-scene tick counts** (`WASMBENCH_RESULT.scenes[]`) — are
emitted ONLY by a bundle built with the benchmark opt-in. In the engine source
the emission is `option_env!`-gated (historically `DCL_WASM_BENCHMARK` →
`WASMBENCH_RESULT` in `web.rs`). A **stock** bundle:

- never prints a `WASMBENCH_RESULT` line, so
- every `orbit_cpu` capture just hits its deadline (empty pair), and
- there are no structured per-scene tick counts.

**This build is ABSENT from this checkout.** Until it lands, web measurement is:

- **`submitFps`** — the fallback in `web-bench-ab.sh` (`DCL_WEB_METRIC=submitfps`,
  the default): wrap `GPUQueue.submit`, count submissions/second. A real,
  low-effort GPU-throughput proxy that works on **any** WebGPU bundle today.
- **functional-only** — `product-tour-web.sh` classifies load/run/error per scene
  from the plain console (panics/wgpu via `cdp_exc.py`), not per-scene counts.

## What "building it" entails (the recipe to un-gate)

Building the benchmark bundle into THIS engine is the prerequisite for the orbit
+ per-scene-tick path. The shape (adapt flag names to the current `web.rs`):

```bash
# Inside the dcl-shell / FHS shell, in bevy-explorer:
DCL_WASM_BENCHMARK=1 DCL_WASM_BENCHMARK_FRAMES=300 \
  wasm-pack build --target web --release -- --features=livekit
# then serve deploy/web (COOP/COEP) and point web-bench-ab.sh / product-tour-web.sh at it.
```

(See the memory note "Bevy-explorer wasm build" for the working store/toolchain
recipe: rust 1.96 store + `RUSTC_BOOTSTRAP` + protoc + libudev pkgconfig +
`wasm-pack --features=livekit`.)

## Why this matters for honesty

The rig never claims an orbit/tick number it cannot produce. Every script that
depends on this build SAYS SO at the point of use:

- `web-bench-ab.sh` defaults to `submitfps` and prints a NOTE if you select
  `orbit` (empty pairs = "you need this build").
- `product-tour-web.sh` reports functional load/run per scene and states the
  tick-count gate in its ledger header.
- `rig/analysis/README.md` says web metrics are submitFps-only until this lands.

Two independent reasons web measurement is partial today, kept separate so
neither is overstated:

1. **This build is absent** (this doc) → no orbit_cpu / per-scene ticks.
2. **The worker-gap** (`../docs/bridge-status.md`) → streams (chat/friends/mic)
   have no main-thread path regardless of the build.

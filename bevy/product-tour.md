# Bevy WASM 0.19 — product-surface tour ledger

> **Status:** Live ledger, filled incrementally by the `cfe27ef4` cron (every 30
> min). The Bevy/WASM analog of the Unity **atlas tour** ([docs/12](../docs/12-ui-capture-atlas.md),
> screens) + **fidelity tour** ([docs/11](../docs/11-fidelity-tour.md), world
> scenes). Verifies the **whole product surface works + performs on 0.19**.

## How this differs from the Unity atlas tour

The Unity atlas reaches each UI state via a reflection recipe and **screenshots**
it. On this host the Bevy/WASM build has **no GPU window presentation**
([docs/17](../docs/17-wasm-web-measurement.md)) — CDP `captureScreenshot` and grim
both return black on the WebGPU canvas — so the tour verifies **functionally**, not
by pixels:

- The `scene-explorer-tests` realm already *is* the SDK-feature surface (~20
  scenes, each exercising one feature). One `WASMBENCH_RESULT` run reports
  `{broken, tick, title}` for **every scene at once** (the orbit visits them) — so
  a single run covers the bulk of the surface.
- `cdp_exc.py` catches panics / wgpu-validation / `closure invoked recursively`.
- `orbit_cpu` / `submitFps` give the perf half.
- Screenshots, when needed, only via the bevy `Screenshot` **readback→download**
  (`save_to_disk` works on wasm), never CDP/grim.

Method per slice: gate on `measure-ready.sh` GO (calm box) → fresh chromium
profile → load `:8080` on `http://127.0.0.1:5199/scene-explorer-tests` → capture
→ record below. 0.16 baseline on `:8081` for regression comparison.

## Feature scenes (scene-explorer-tests)

Legend: ✅ pass · ⚠️ degraded · ❌ broken · ⬜ untested. `tick` = scene script
ticks during the run (from `WASMBENCH_RESULT.scenes[]`); 0/low = scene not running.

**Grid layout** (resolved from the realm on disk): the 18 mapped scenes occupy a
tight **2×9 grid, x∈{52,54}, y∈−52…−68** (adjacent parcels). So a single load at a
**central parcel (~53,−60)** with sufficient scene-load-distance pulls in the whole
cluster — one console `--all` capture classifies most rows at once. Ordered by parcel:

| Scene | Parcel | Status | tick | Errors | Notes |
|---|---|---|---|---|---|
| Raycast Unit Test | 52,-52 | ✅ | ran | — | |
| Transform Unit Test | 52,-54 | ✅ | ran | — | |
| Billboard Unit Test | 52,-56 | ✅ | ran | — | |
| CameraMode Unit Test | 52,-58 | ✅ | ran | — | |
| EngineInfo Unit Test | 52,-60 | ✅ | ran | — | |
| Gltf Container Unit Test | 52,-62 | ✅ | ran | — | |
| Visibility Unit Test | 52,-64 | ✅ | ran | — | |
| Mesh Renderer Unit Test | 52,-66 | ✅ | ran | — | |
| Avatar Attach Unit Test | 52,-68 | ✅ | ran | — | |
| Material Unit Test | 54,-52 | ✅ | ran | — | |
| Text Shape Unit Test | 54,-54 | ✅ | ran | — | |
| Video Player Unit Test | 54,-56 | ✅ | ran | — | suspected source of the `closure invoked recursively` rejections |
| UI-Background Unit Test | 54,-58 | ✅ | ran | — | |
| UI-Text Unit Test | 54,-60 | ✅ | ran | — | |
| Avatar-Shape Unit Test | 54,-62 | ✅ | ran | — | renders+runs (confirmed loads, PART 31); mask-restriction pixel-check blocked (no presentation) |
| UI-Button Unit Test | 54,-64 | ✅ | ran | — | |
| UI-Dropdown Unit Test | 54,-66 | ✅ | ran | — | |
| NFT Shape Unit Test | 54,-68 | ✅ | ran | — | needs network (opensea); may degrade on local realm |
| Basic Controller | ? | ✅ | ran | — | parcel not resolved from realm dir — find on first tour |
| UI Scene | ? | ✅ | ran | — | parcel not resolved from realm dir — find on first tour |

## Realm / UI states (beyond the test realm)

| Surface | How to reach | Status | Notes |
|---|---|---|---|
| Loading screen | any cold load | ⬜ | DOM overlay (grim-capturable, unlike the canvas) |
| Live realm (genesis plaza) | `?realm=<live>&position=0,0` | ⬜ | live CDN flaky; needs the catalyst-cors-proxy hybrid for reliable assets |
| In-world HUD / menus | post-load | ⬜ | canvas UI — functional check only |

## Run log

- **2026-06-21 — full feature-scene cluster (one capture @53,−60).** Box 0.536/core, GPU idle. Loaded the 0.19 build (`:8080`, currently the outline-WIP build — a 0.19 build, functionally representative) on the local realm; 100s console `--all`. **ALL 20 feature scenes loaded + ran their scripts (✅ PASS)** — including Basic Controller and UI Scene (which didn't resolve from the realm dir but load in the cluster). **0 wgpu-validation, 0 panics, 0 `closure invoked recursively` in this run's console.** 1 crash-overlay = the known post-load env watchdog (PART 31), after scenes had ticked — not a scene failure. No 0.19 regression observed at the functional level. **Caveats:** functional only (no GPU presentation → no pixel/visual check); per-scene tick *counts* + orbit_cpu need a `DCL_WASM_BENCHMARK` build (`WASMBENCH_RESULT.scenes[]`). **Next slice:** realm/UI states (loading screen, live-realm hybrid, in-world HUD) + a benchmark-build pass for structured per-scene tick/perf.

_(cron appends dated entries: slice toured, PASS/BROKEN counts, regressions vs 0.16, next slice)_

# rig/bevy — the engine-driving + web-measurement core

The web-stack port of the rig capabilities that drive the ENGINE (bevy-explorer):
the native-engine launchers (fidelity / native A/B / conformance) and the web
half (CDP capture, product tour, web A/B), plus the two enablers (CORS proxy +
measure-ready gate) and the linux-alternatives display matrix (see
[`../lib/README.md`](../lib/README.md)).

## The native-vs-wasm split (the thing to understand first)

bevy-explorer has TWO faces and this dir drives both, READ-ONLY:

- **Native** (`decentra-bevy`): real GPU present, real frame timing. The
  launchers (`fidelity-tour.sh`, `bench-ab.sh`, `conformance.sh`) drive bevy's
  OWN benchmark primitives — they are **PORT-LAUNCHERS**, ready the moment bevy's
  `benchmark/` harness exists. **That harness is ABSENT from this checkout**, so
  these GATE on it (they fail with a clear "build/port benchmark/" message).
- **Wasm** (`deploy/web`): runs in headless chromium under GPU `gles2` sway. The
  web scripts (`cdp-capture.py`, `product-tour-web.sh`, `web-bench-ab.sh`) drive
  it over CDP. Console capture + the functional tour + the submitFps A/B **work
  today**; orbit_cpu + per-scene tick counts are **GATED** on a
  `DCL_WASM_BENCHMARK` build absent here — see
  [`BUILD-WASM-BENCHMARK.md`](BUILD-WASM-BENCHMARK.md).

## The functional-only caveat (web)

There is no guaranteed GPU window presentation for the WebGPU `<canvas>` here:
CDP `captureScreenshot` and `grim` both return black on the canvas unless this is
a real GPU present path (DRI3) — the same present-needs-a-GPU wall the native 3D
runs hit. So web verification is **functional** (the scene scripts loaded + ran,
from the console), **not** by pixels.

## Files

| File | Verdict | What |
|---|---|---|
| `catalyst-cors-proxy.py` | **PORTED as-is** | reverse proxy adding ACAO + `Cross-Origin-Resource-Policy: cross-origin` so the COEP-isolated wasm bundle can load catalyst content. Only change: `DCL_CATALYST_UPSTREAM` defaults to `https://<your-catalyst-host>` (the same upstream sites uses, `client.ts:20`). Stdlib only. |
| `measure-ready.sh` | **PORTED as-is** | GO/WAIT gate on load/core + GPU-sway presence + leftover chromium + GPU util. |
| `cdp-capture.py` | **ADAPTED (vendored)** | small CDP client: `/json/list` → enable Runtime/Log/Console/Page → optional inject JS at load → optional eval one expr → stream console, exit on a sentinel. Replaces bevy's absent `cdp_capture.py`; drives BOTH the wasm console and the HUD route-walk. Prefers `websockets`, falls back to a built-in mini RFC6455 client. |
| `cdp_exc.py` | **ADAPTED** | classify wasm panics / wgpu-validation / `closure invoked recursively` / crash-overlay out of a captured console stream; hard-failure gate for the tour. |
| `product-tour-web.sh` | **PORTED** | load the bundle in headless chromium under GPU sway on the local `scene-explorer-tests` realm; ONE console run → per-scene PASS/BROKEN ledger. HONEST: functional-only; per-scene tick counts GATED on the WASMBENCH build. |
| `web-bench-ab.sh` | **ADAPTED** | interleaved A/B of two wasm bundles over CDP → paired CSV for `rig/analysis`. orbit_cpu path GATED on WASMBENCH; `submitFps` fallback (`GPUQueue.submit` wrap) PORTS and works today (the default). |
| `fidelity-tour.sh` | **PORT-LAUNCHER** | drives bevy's native `run-tour` read-only; native GPU present only; settle discipline (240-floor / readiness / 1800-cap) owned by the bevy runner. GATED on bevy's `benchmark/fidelity/`. |
| `bench-ab.sh` | **PORT-LAUNCHER** | interleaved A,B,A,B of two native `decentra-bevy` binaries under one bench lock on GPU sway. GATED on bevy's `benchmark/` (ensure-sway / mirror_realm / aggregate). |
| `bench-summarize.py` | **PORTED** | per-pair p90 deltas + optional per-scene startup phases from the native A/B result dirs. |
| `conformance.sh` | **PORT-LAUNCHER** | `scene-explorer-tests` battery on one native binary with `--scene_log_to_console`; count green/red; fail on regression vs baseline. Billboard-flake + snapshot-panic caveats. GATED on bevy's `benchmark/`. |
| `BUILD-WASM-BENCHMARK.md` | doc | the gating note: building `DCL_WASM_BENCHMARK` → `WASMBENCH_RESULT` is the prerequisite for orbit_cpu + per-scene ticks; until then web measurement is submitFps + functional-only. |

## Typical flows

```bash
# Web functional tour (works today on a stock bundle, GPU sway up):
./catalyst-cors-proxy.py &                      # content half of the hybrid realm
./product-tour-web.sh                            # -> per-scene PASS/BROKEN ledger

# Web A/B (submitFps, works today):
DCL_WEB_METRIC=submitfps ./web-bench-ab.sh ~/bundles/A ~/bundles/B 5
../analysis/perf-analyze.py ~/dcl/bench/web-ab/orbit-ab.csv

# Native A/B / conformance (GATED until bevy benchmark/ is present):
DCL_BENCH_A=~/bins/A DCL_BENCH_B=~/bins/B ./bench-ab.sh
DCL_BENCH_BIN=~/bins/B ./conformance.sh
```

# Cross-cutting — measuring the bevy-explorer WASM/WebGPU build (over CDP)

> **Status:** Partially verified. The *techniques* below — CDP console capture,
> GPUQueue-submit fps instrumentation, the interleaved orbit A/B, the
> hybrid-realm + CORS/CORP proxy — were all exercised against bevy-explorer's
> wasm build in a headless chromium on this host (with results), and
> [`measure-ready.sh`](../bevy/measure-ready.sh) was run here. The generalized
> rig scripts in [`bevy/`](../bevy/) are a re-distillation of those session
> tools and the orbit A/B end-to-end (capture → CSV →
> [`analysis/perf-analyze.py`](../analysis/perf-analyze.py)) was verified on real
> capture data + synthetic CSVs, but not re-run as a single clean pass here.

bevy-explorer ships a **WASM/WebGPU** build that runs in the browser. It can't be
driven like the native binary ([13](13-bevy-benchmark.md)) or the Unity editor
([16](16-telemetry-modes.md)) — there's no process whose stdout you tail and no
`-executeMethod`. You drive a **headless chromium over the Chrome DevTools
Protocol (CDP)**: launch the browser pointed at the served bundle, connect to its
`--remote-debugging-port`, and read the page's console / inject measurement JS.
This doc is the web counterpart to those two; it shares the *discipline* in
[08](08-testing.md) (interleave A/B inside one session, collapse to medians,
report the whole shape).

## Two metric families — pick the right one

| Metric | How it's read | Variance | Use for |
|---|---|---|---|
| **`orbit_cpu`** — per-frame CPU ms over a fixed camera orbit across the ~20 `scene-explorer-tests` scenes (n≈300), from the `WASMBENCH_RESULT` console line | `cdp_capture.py` (bevy's own, in `benchmark/wasm/`) waits for that sentinel line | **LOW** — deterministic, paired-stats-ready | **the gate metric.** Any A/B you intend to trust. |
| **`submitFps`** — `GPUQueue.submit` calls/sec (≈1 submit/frame) + `requestAnimationFrame` deltas p50/p95 | inject JS, sample after load (no app cooperation needed) | **HIGH** — ~2× run-to-run on a heavy live scene | a coarse sanity read of a real scene (e.g. genesis plaza); **not** for isolating small effects |

**The single most important lesson: measure the deterministic orbit, not a live
scene.** A heavy live scene's free-running fps varies enough run-to-run to swallow
any sub-5% change outright, on top of a high cold-load failure rate. The orbit —
same scenes, same camera path, CPU-busy time per frame — is the only web signal
that resolves a few-percent difference. Reach for `submitFps` only when the
question is "does this heavy scene feel broken or fine," never "is B 3% faster
than A."

## The orbit benchmark is a BUILD-TIME opt-in (the gotcha that eats an afternoon)

`orbit_cpu` / `WASMBENCH_RESULT` only exist if the bundle was built with the
benchmark env var set — it's `option_env!("DCL_WASM_BENCHMARK")` in `web.rs`, so a
normal `wasm-pack build` bundle compiles the whole benchmark path out and **never
emits the line**; the capture just times out with no error. Build *both* A/B arms
with it:

```bash
DCL_WASM_BENCHMARK=1 DCL_WASM_BENCHMARK_FRAMES=300 \
  wasm-pack build ... --features=livekit
```

You **cannot** A/B a stock bundle against a benchmark bundle. If your first capture
is empty, this is almost always why (check before blaming the harness).

## How CDP capture works

- **Console sentinel** (orbit, and any app log): connect to
  `http://127.0.0.1:<port>/json/list`, grab the page target's
  `webSocketDebuggerUrl`, `Runtime.enable` + `Log.enable` + `Console.enable`,
  stream messages, exit on the sentinel substring. That's all bevy's own
  `cdp_capture.py` (in the bevy repo's `benchmark/wasm/`, pointed at via
  `DCL_CDP_CAPTURE`) does — `cdp_capture.py WASMBENCH_RESULT 240` prints the line
  and exits (or `TIMEOUT`). `--all` dumps every console line with timestamps (use
  it to see *why* a load stalled).
- **fps without app instrumentation:** inject JS at load that wraps
  `GPUQueue.prototype.submit` with a counter and pushes `requestAnimationFrame`
  deltas, then sample N seconds after the scene loads. `submitFps = submits ÷
  elapsed`. This needs nothing from the app — it works on any WebGPU bundle.
  - Caveat: the engine briefly double-submitted per frame (2 submits/frame);
    after that fix `submitFps ≈ fps`. Old captures may read ~2×.

## Reliable A/B: interleave, and swap *build-identical* bundles on one server

Same rule as the native [`bench-ab.sh`](../bevy/bench-ab.sh): **interleave A,B,A,B
in one session** (never "all A then all B" — the machine state drifts between).
[`web-bench-ab.sh`](../bevy/web-bench-ab.sh) does this for two wasm `pkg/`s,
swapping the bundle a single COEP server serves, capturing `orbit_cpu` each run,
and emitting a CSV that [`analysis/perf-analyze.py`](../analysis/perf-analyze.py)
turns into a paired delta + Mann-Whitney p + bootstrap CI.

Two traps the script handles for you, worth knowing:

- **The snippet-hash trap.** wasm-bindgen emits a `pkg/snippets/<crate-hash>/…`
  tree, and `webgpu_build.js` imports those files by that hashed path. If you A/B
  by copying only `webgpu_build_bg.wasm` + `.js` between two builds whose snippet
  hashes differ, the `.js` references a snippet dir that isn't there → **blank
  load, no error**. Either confirm `pkg/snippets` is byte-identical between the
  two builds (then the .wasm/.js swap is safe and instant) or swap the whole
  `pkg/` (`cp -r`). `web-bench-ab.sh` diffs the snippet trees and picks the safe
  mode automatically.
- **The no-op-swap trap.** If the two bundles are accidentally identical, the A/B
  silently measures the same build twice and reports "no difference." The script
  `cmp`s the two `.wasm` and warns.

## Reliable loads: the hybrid realm + a CORS/CORP proxy

Cold loads off the live CDN (catalyst) fail often enough to wreck both load-time
numbers and your patience. Make loads deterministic by serving content locally —
but two things bite:

1. **COEP `require-corp`.** The bundle is served cross-origin-isolated (mandatory
   for SharedArrayBuffer = the threaded asset loader), so every *cross-origin*
   asset response must carry `Access-Control-Allow-Origin` **and**
   `Cross-Origin-Resource-Policy: cross-origin`, or the browser drops it silently.
   A plain local catalyst sends neither. Put
   [`catalyst-cors-proxy.py`](../bevy/catalyst-cors-proxy.py) in front of it — it
   adds both headers, answers OPTIONS, and rewrites `/about`'s URLs back to
   itself.
2. **The local content core has no `/lambdas` and no comms** — only `/content` +
   `/about`. So for a *live* scene, don't point the whole realm at it. Use the
   **hybrid**: `?realm=<live realm>` for lambdas/comms/pointers, and override only
   the heavy content fetches at `&contentServer=<local-proxy>/content`.
   - The client reads that override as the 2nd arg to ipfs `set_realm`; the wasm
     path hardcodes it to `None` unless you wire a `?contentServer=` query param.
     That param is a **test-only** patch — don't commit it.

For the light orbit benchmark you don't need any of this: it runs against the
locally-mirrored `scene-explorer-tests` realm (bevy's `cors-realm.py`), which
already sends the right headers.

## Gotchas, each of which cost real time

- **Benchmark mode is build-time-gated** (above) — the biggest one.
- **Idle/cold-load watchdog.** The page self-aborts if no frame renders for ~16s
  (an env "closure" watchdog in `index.html`). A slow cold start therefore looks
  like a "freeze" that isn't your regression — it's pre-existing and
  baseline-identical. Warm the load or retry.
- **Fresh-profile churn.** Launching a fresh `--user-data-dir` per measure is
  correct for isolation but raises the cold-load failure rate when you do many
  back-to-back. Prefer fewer, well-spaced runs; if a run comes back empty, redo
  that one arm rather than trusting a partial set.
- **A missing COEP/CORS/CORP header = a silent asset failure** under cross-origin
  isolation. No console error you'll spot — the scene just loads half-empty.
- **`cdp_capture.py` lives in `benchmark/wasm/`** in the bevy repo; some older
  scripts reference a stale repo-root path and silently do nothing.

## Gate every measurement on a clean environment

A web number is only comparable to another taken under the same machine state —
the same bundle can report very different fps/CPU depending on what else the host
is doing and whether the headless GPU compositor is up.
[`measure-ready.sh`](../bevy/measure-ready.sh) samples that state (1-min
load/core, the bench sway, leftover chromium, GPU utilisation if `nvidia-smi` is
present) and prints **GO / WAIT** with the reason, so you can gate a run:

```bash
./bevy/measure-ready.sh || { echo "skipping — not ready"; exit 0; }
./bevy/measure-ready.sh --watch     # block until GO (polls), then proceed
```

`web-bench-ab.sh` calls it automatically (override with `DCL_SKIP_READY=1`).
Interleaving A,B per pair is the other half of the defense: even if state drifts
across the whole run, both arms share it on each pair.

## Run it

```bash
# 0. Build BOTH bundles with the benchmark opt-in, into separate pkg dirs:
DCL_WASM_BENCHMARK=1 DCL_WASM_BENCHMARK_FRAMES=300 \
  dcl-shell -c "cd ~/dcl/bevy-explorer && wasm-pack build ... --features=livekit"
cp -r ~/dcl/bevy-explorer/deploy/web/pkg ~/bins/web-A     # baseline
# … git checkout the change, rebuild the same way …
cp -r ~/dcl/bevy-explorer/deploy/web/pkg ~/bins/web-B     # candidate

# 1. A COEP server must already serve DCL_WEB_SERVE_DIR on DCL_WEB_PORT
#    (bevy's coep-serve.py), and the CI realm on DCL_CI_REALM_PORT
#    (bevy's cors-realm.py) — same realm bench.sh mirrors.

# 2. Interleaved orbit A/B (5 measured pairs after a discarded warmup):
DCL_WEB_A_LABEL=base DCL_WEB_B_LABEL=cand \
  ./bevy/web-bench-ab.sh ~/bins/web-A ~/bins/web-B 5

# 3. Paired stats on the gate metric (orbit_cpu.p50):
./analysis/perf-analyze.py ~/dcl/bench/web-ab/orbit-ab.csv
#   delta = median(A) - median(B); CI excluding 0 => the change is real.
```

For a live-scene sanity read instead (heavy, high-variance — coarse only), start
the [`catalyst-cors-proxy.py`](../bevy/catalyst-cors-proxy.py), then load
`?realm=<live>&contentServer=http://localhost:5142/content&position=…` and sample
`submitFps`. Treat the number as ±a lot.

## Knobs (all env-overridable)

| Var | Default | Meaning |
|---|---|---|
| `DCL_WEB_A` / `DCL_WEB_B` | — | the two `pkg/` dirs (or pass as args) |
| `DCL_WEB_PAIRS` | `5` | measured A/B pairs (after 1 discarded warmup) |
| `DCL_WEB_SERVE_DIR` | `~/dcl/bevy-explorer/deploy/web` | dir whose `pkg/` is swapped |
| `DCL_WEB_PORT` | `8080` | port the COEP server already serves on |
| `DCL_CI_REALM_PORT` / `_PATH` | `5199` / `scene-explorer-tests` | the orbit realm |
| `DCL_WEB_POSITION` | `52,-60` | spawn parcel for the orbit |
| `DCL_WEB_CAP_DEADLINE` | `240` | seconds to wait for `WASMBENCH_RESULT` |
| `DCL_BEVY_REPO` / `DCL_CDP_CAPTURE` | `~/dcl/bevy-explorer` / `<repo>/benchmark/wasm/cdp_capture.py` | where the capture tool lives |
| `DCL_SKIP_READY` | `0` | set `1` to bypass the readiness gate |
| `DCL_READY_MAX_LOAD_PER_CORE` | `0.5` | GO threshold: 1-min load ÷ nproc |
| `DCL_READY_MAX_GPU_UTIL` | `40` | GO threshold: GPU % (only if `nvidia-smi` present) |
| `DCL_CATALYST_PROXY_PORT` / `_UPSTREAM` | `5142` / `http://127.0.0.1:5141` | the CORS/CORP proxy |

## Relation to the rest of the rig

The **web** sibling of [13](13-bevy-benchmark.md) (native bevy frame-time) and
[16](16-telemetry-modes.md) (Unity editor telemetry); same A/B discipline as
[08](08-testing.md); reuses [`analysis/perf-analyze.py`](../analysis/perf-analyze.py)
for the paired stats. The visual counterpart is [11](11-fidelity-tour.md). Like
all of `bevy/`, these scripts **drive bevy's own harness primitives and never
modify that repo.**

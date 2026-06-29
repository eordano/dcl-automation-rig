# Cross-cutting — bevy-explorer benchmark (A/B perf + conformance)

> **Status:** Not verified. Drives an external (bevy-explorer) harness; not run
> from this rig. The summarizer *was* run here against existing A/B result data.
> The benchmark binary's `--benchmark*` flags below (crowd, capture, frames)
> live in the **perf-campaign branch** of bevy-explorer, not the upstream org
> mirror on this host — they're documented from the campaign playbook, not
> re-verified from on-disk source. The one engine fact they hinge on (a 64-frame
> atmosphere dither cycle ⇒ settle ~90 frames before a visual capture) *is*
> confirmed in upstream (`crates/.../shaders/nishita_cloud.wgsl`).

The bevy-explorer repo owns a deterministic rendering benchmark
(`benchmark/bench.sh` → `decentra-bevy --benchmark`, against a locally mirrored
`scene-explorer-tests` realm, p90 frame-time aggregation). That covers measuring
**one** binary. This rig adds the orchestration *on top of* it — same rule as
[`bevy/fidelity-tour.sh`](../bevy/fidelity-tour.sh): **drive bevy's own
primitives, never modify that repo.**

Three thin launchers in [`bevy/`](../bevy/):

| Script | What it does |
|---|---|
| [`bench-ab.sh`](../bevy/bench-ab.sh) | **Interleaved** A/B frame-time benchmark of two prebuilt binaries |
| [`conformance.sh`](../bevy/conformance.sh) | Run the scene-test battery, count 🟢/🔴, diff a baseline |
| [`bench-summarize.py`](../bevy/bench-summarize.py) | A/B diff table: load secs, hitches, per-scene startup phases |

## Why A/B is interleaved (the one idea that matters)

`bench.sh` measures one binary as N runs back-to-back, so comparing two commits
means "label A at 10:00, label B at 10:08". Over those minutes the GPU heats and
background load drifts, and that drift gets baked into the A↔B delta.
`bench-ab.sh` instead runs **one pair at a time** — A, B, A, B … — under a single
bench lock, so both binaries see the same thermal/noise state on every pair.
Everything else (the discarded warmup pair, the `flock /tmp/dcl-bench.lock`
serialization, the GPU headless sway, `aggregate.py`) is reused verbatim from the
bevy harness.

## Run it

```bash
# 0. Point at your bevy checkout (default ~/dcl/bevy-explorer).
export DCL_BEVY_REPO=~/dcl/bevy-explorer

# 1. Build the two binaries you want to compare into separate dirs.
#    (Build the dcl_deno_ipc sidecar once; bench-ab.sh takes it from
#     <bevy>/target/release.)
dcl-shell -c "cd $DCL_BEVY_REPO && cargo build --release"
mkdir -p ~/bins/A ~/bins/B
cp "$DCL_BEVY_REPO/target/release/decentra-bevy" ~/bins/A/    # baseline commit
# … git checkout the change, rebuild …
cp "$DCL_BEVY_REPO/target/release/decentra-bevy" ~/bins/B/    # candidate commit

# 2. Interleaved A/B (8 measured pairs after a discarded warmup pair):
DCL_BENCH_A_LABEL=base DCL_BENCH_B_LABEL=cand \
  ./bevy/bench-ab.sh ~/bins/A ~/bins/B 8

# 3. Read the diff:
./bevy/bench-summarize.py ~/dcl/bench/results/ab-base ~/dcl/bench/results/ab-cand base cand

# Conformance of one binary (regression-gates against the bevy baseline if present):
DCL_BENCH_BIN=~/bins/B ./bevy/conformance.sh
```

## Prerequisites the launchers check for you

- **A bevy-explorer checkout** with its `benchmark/` harness present
  (`ensure-sway.sh`, `aggregate.py`, `mirror_realm.py`). `bench-ab.sh`/
  `conformance.sh` fail fast if it's missing.
- **Two built binaries** for A/B (`<dir>/decentra-bevy` each) plus the
  `dcl_deno_ipc` sidecar in `<bevy>/target/release`.
- **A GPU-rendered headless sway** — `ensure-sway.sh` brings it up
  (`WLR_RENDERER=gles2`, a high-refresh headless output). This is deliberately
  **not** the rig's pixman software sway, which throttles presents to a low frame
  rate and would mask every rendering difference. See
  [`03-linux-alternatives.md`](03-linux-alternatives.md) and the fidelity-tour doc.
- **The `dcl-shell` FHS wrapper** (`DCL_SHELL`, default `dcl-shell` on `PATH`;
  set it to your wrapper if elsewhere) — the bevy binary is a glibc ELF.
- The realm is **mirrored + served on loopback** automatically on first run
  (idempotent; mirrors what `benchmark/bench.sh` does) so network jitter never
  enters the frame times.

## Knobs (all env-overridable)

| Var | Default | Meaning |
|---|---|---|
| `DCL_BEVY_REPO` | `~/dcl/bevy-explorer` | bevy checkout (owns `benchmark/`) |
| `DCL_BENCH_A` / `DCL_BENCH_B` | — | binary dirs (or pass as args) |
| `DCL_BENCH_A_LABEL` / `_B_LABEL` | `A` / `B` | result-dir + summary labels |
| `DCL_BENCH_PAIRS` | `8` | measured A/B pairs (3rd arg) |
| `DCL_BENCH_FRAMES` | `600` | measured frames per run |
| `DCL_BENCH_LOCATION` / `_DISTANCE` | `52,-60` / `300` | camera placement |
| `DCL_BENCH_RES` | — | e.g. `3840x2160` for a GPU-bound run |
| `DCL_CONF_SCENES` | 14-scene grid | `;`-separated `x,y` for conformance |
| `DCL_CONF_BASELINE` | `<bevy>/benchmark/conformance-baseline.txt` | regression gate |
| `BENCH_PORT` / `BENCH_REALM_DIR` / `BENCH_RESULTS_ROOT` | `5199` / `~/dcl/bench/realm` / `~/dcl/bench/results` | realm + output (match the bevy harness) |
| `BENCH_CPUS` | `40-47` | `taskset` pin for the measured process |
| `DCL_BENCH_FAKE_PLAYERS` | `0` | inject N synthetic crowd players (`--benchmark_fake_players N`); forwarded only when >0 — crowd/comms changes are invisible at N=0 |

## The optional per-scene startup table

`bench-summarize.py` always prints the frame-time headlines (load secs, >50/>100ms
loading hitches, loading p50). It *additionally* prints a per-scene startup table
(js KB, runtime / loader / total ms per phase) **only if** the binary emits
`[scene-startup] …` log lines — an optional instrumentation. Stock builds don't,
and the table is simply skipped; no error.

## The verdict: paired delta vs the noise floor

A bundled "branch is X% faster" number doesn't justify a per-change claim. The
trustworthy verdict is, **per pair**, the run-p90 of each side, then the
**median of the paired (B−A) deltas** — compared to a noise floor you establish
by A/B-ing two builds of the *same* commit (it was **≈ ±0.08 ms** on the
campaign rig; re-establish it if the machine state changed). A delta only counts
when it exceeds that floor; anything smaller is "no regression," never a
fabricated percentage. Also report the whole pooled shape (p25/p50/p75/p90/p99)
of each side, not just the headline — a change can be median-neutral but
tail-heavy.

**Calibrate the claim to what you measured** — not everything resolves on one
mode:

- steady-state changes (schedules, executor, thread caps) → measure at **N=0**;
- crowd/broadcast changes → only show with `DCL_BENCH_FAKE_PLAYERS=100`;
- loading changes → read `load_secs` / loading-hitch counts, which are NEUTRAL on
  steady-state `frame_ms` (frame them as load-time, not "−X% frame");
- genuinely tiny ones → "removes work / no regression," never a made-up number.

## Crowd scaling (the N=0 vs N=100 split)

Many changes (per-packet decode, CRDT broadcast, avatar spawn/animation) are
**invisible at N=0** — there's no one in the scene. `DCL_BENCH_FAKE_PLAYERS=N`
injects N synthetic foreign players through the *real* comms path (10 Hz
movement, default profiles, walking circles at the orbit centre), so the crowd
systems actually run:

```bash
DCL_BENCH_FAKE_PLAYERS=100 DCL_BENCH_A_LABEL=base DCL_BENCH_B_LABEL=cand \
  ./bevy/bench-ab.sh ~/bins/A ~/bins/B 8
```

`bench-ab.sh` forwards the flag only when >0 (a stock binary without
`--benchmark_fake_players` would otherwise abort on the unknown arg). Run the
*same* count on both arms; a crowd win measured at N=100 is not a steady-state
win, and vice-versa.

## Visual regression (capture mode)

The orbit is **frame-index-keyed**, so the same index renders the same camera
pose across binaries — which makes deterministic pixel comparison possible.
`--benchmark_capture i,j,k` parks the orbit at those frame indices, **freezes
time-of-day**, suppresses the MOTD dialog, screenshots each frame beside the
report JSON, and exits:

```bash
<bin> --server … --benchmark out.json --benchmark_frames 1500 \
      --benchmark_capture 100,350,600,850,1100,1350
benchmark/frame_diff.sh /tmp/cap/base1 /tmp/cap/base2          # NOISE FLOOR (base vs base)
benchmark/frame_diff.sh /tmp/cap/base1 /tmp/cap/opt /tmp/cap/heat   # change + heatmaps
```

`frame_diff.{sh,py}` (in the bevy harness) reports per-frame SSIM / PSNR /
max-pixel-diff + amplified heatmaps. **Capture base-vs-base first** — a frame
only signals a real visual change when it exceeds that base-vs-base floor. The
capture settles ~90 frames before shooting (past the **64-frame** atmosphere
dither cycle, confirmed in `nishita_cloud.wgsl`) and freezes the sun in
`PreUpdate` *before* it's read; without those, the comparison is dominated by
lighting/dither drift. Don't expect bit-equality even base-vs-base — deband +
atmosphere dither + FP non-determinism leave a small floor (the sky is
inherently noisy). This is the bevy counterpart to the Unity
[deterministic-capture bundle](00-shared.md) and the generic discipline in
[08](08-testing.md).

## Conformance: gate against a control, not an absolute

`conformance.sh` counts 🟢/🔴 and (if a `conformance-baseline.txt` is present)
fails non-zero on a regression. Two caveats from the campaign so a flake doesn't
read as a regression:

- **`billboard BM_Y` / `BM_ALL` flake ~20%** (worse under load) on **all**
  binaries including an unmodified base — an upstream test-tolerance issue, not
  your change. Re-run the single scene isolated (`DCL_CONF_SCENES='52,-56'`)
  before blaming a change.
- **An end-of-run snapshot-infra panic** exits non-zero *after* the tests pass
  on the headless rig (xvfb readback) — present on base too.

So always run the **base binary in the same session** and compare red *sets*,
not absolute counts — the baseline-diff in `conformance.sh` is the floor.

## Gotchas (each cost real time)

- **flock everything.** All measurement (and any manual app run during a bench
  session) holds `flock /tmp/dcl-bench.lock`. `bench.sh` builds outside it.
- **`dcl_deno_ipc` must sit beside `decentra-bevy`** — the client spawns the
  sidecar from its own dir. Copy both into each A/B dir; don't symlink across
  worktrees.
- **Audio on headless.** LiveKit's AudioManager needs a sound server; newer bevy
  pins hard-panic on a missing device. The rig provides one (`rig_ensure_audio` /
  the `~/.asoundrc` workaround).
- **Trace files are huge.** A `bevy/trace_chrome` run drops a multi-GB
  `trace-*.json` in CWD — launch from `/tmp`, never the repo (they've been
  committed by accident; scrub before pushing upstream).
- **Detached long jobs:** `setsid nohup … </dev/null & disown` — the agent
  harness otherwise kills jobs at ~10 min.
- **Load contamination.** A heavy co-tenant inflates absolute numbers; the
  interleaving keeps the *relative* delta valid, but for clean numbers gate the
  run on a quiet box (`until awk '{exit !($1<30)}' /proc/loadavg; do sleep 120;
  done`) and run detached. ([`measure-ready.sh`](../bevy/measure-ready.sh) is the
  GO/WAIT gate the web harness uses; the same idea applies here.)

## Relation to the rest of the rig

This is the **bevy** counterpart to the Unity-side perf tooling. For the Unity
player/editor use the in-editor [`DclPlaytestHarness`](01-linux-editor.md) +
[`analysis/perf-analyze.py`](../analysis/perf-analyze.py) (paired A/B stats) and
[`08-testing.md`](08-testing.md). The visual-fidelity gallery for bevy is the
sibling [`11-fidelity-tour.md`](11-fidelity-tour.md); this doc is its
frame-time/conformance counterpart. This doc covers the **native** binary; for
the **WASM/WebGPU** build (driven in headless chromium over CDP, a different
harness with its own gotchas) see [`17-wasm-web-measurement.md`](17-wasm-web-measurement.md).

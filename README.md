# decentraland-automation-rig

A consolidation of **techniques for building, running, and driving the
Decentraland Unity Explorer headlessly** — the editor and the built player —
across Linux, Windows (VM), and Mac. Distilled from the working source rigs
(the Linux headless rig, the Windows VM control harness, the Unity Explorer
repo's own build scripts, and the testing playbooks) into one set of
non-overlapping, commented files.

Most of it is **adapted from those source rigs, not independently verified on a
clean host** — so this is a curated starting point, not a proven turnkey toolkit.
Only the small subset in the verification ledger below has actually been run here;
treat everything else as design intent. Each doc says which it is.

## Verification status (read this before trusting a doc)

Most of this rig is **adapted from working source rigs but has NOT been
re-verified end-to-end on a clean host** — treat those docs as design intent,
not proof. Each doc carries a `Status:` banner saying which it is. What I have
actually observed working on this machine, with evidence:

| Observed working | Evidence |
|---|---|
| Headless display (sway+wayvnc+Xwayland+bwrap, audio sockets) | brought up + screenshotted + torn down on isolated ports, repeatedly |
| `config.sh` resolution (Unity version/path, FHS wrap, ports) | resolved live; values printed |
| Linux player via Proton — **launcher only** | drove Proton through prefix-populate + exe launch + wine `fsync up and running`; the player then exited (no GPU available for presentation on this host), so **3D render is NOT verified** |
| Linux editor — **boot/license/compile/layout only** | `editor-up.sh` brought up the display, seeded nothing (license already present), excluded the test asmdefs, launched Unity 6000.4.0f1 → splash rendered, **Unity Pro license accepted**, all scripts compiled, editor *layout* loaded — then Vulkan hit `No DRI3 support — required for presentation` and the editor exited. `-force-glcore` fails earlier (no GLX on the software Xwayland). **A rendered, driveable editor window is NOT verified on this host** (needs a presenting GPU). |
| VM launch with audio + mic | QEMU up, pipewire showing guest playback + `Blue Microphones` capture, both active |
| VM control round-trips | `vm/vmssh.sh` and `vm/vmkeys.sh` screendump (1280×800 PNG). `vm/vmclick.sh` (QMP mouse) is **not** verified |
| `analysis/perf-analyze.py` | run on synthetic A/B data; correct significant/non-significant verdicts |
| `analysis/perf-analyze-multi.py` | run on synthetic multi-knob data; correctly ranked shadows/msaa as costs and SRP batcher as a net win (negative delta) |
| Batchmode AB conversion (the *technique*, docs/14) | the abgen reference corpus was generated this way on this host — converter on Unity 6000.2.6f2, `-nographics` batchmode, Win64 + OSX, thousands of bundles, with the watchdog + serialization in play. The generalized `convert/` scripts here are a re-distillation and are **not** re-verified as-is. |
| WASM/WebGPU measurement over CDP (the *techniques*, docs/17) | exercised against bevy-explorer's wasm build in headless chromium on this host: CDP console capture of the deterministic `orbit_cpu` benchmark, `GPUQueue.submit` fps instrumentation, the interleaved orbit A/B (capture → CSV → `perf-analyze.py`, verified on real + synthetic data), and the hybrid-realm + CORS/CORP proxy for deterministic local-content loads. `bevy/measure-ready.sh` was run here. The generalized `bevy/web-*` scripts are a re-distillation, not re-run as one clean pass. |
| `bevy/measure-ready.sh` | run here — correctly reported load/core, the bench sway, leftover-chromium count, and GPU util, and flipped GO↔WAIT as the load-per-core threshold crossed the live 1-min average. |

The Linux editor + Proton player share one wall: **no GPU window presentation on
this host**. Both get all the way to the GUI step, then can't present. Verifying a
rendered editor or player needs a presenting GPU (a GPU-backed sway with DRI3).

**Not verified here** (adapted from source, unrun): ClaudeIPC driving + the warm
flow (the editor exits before they'd run), the native Linux player, all Windows
build/run, mac, the autonomous loop, end-to-end auth signing, AltTester, and the
fidelity tour.

## The targets

| # | Target | What it is | Entry points |
|---|--------|------------|--------------|
| 1 | **Linux editor** | Unity Editor, headless, on a nested display | [`linux/editor-up.sh`](linux/editor-up.sh) · [docs](docs/01-linux-editor.md) |
| 2 | **Linux binary** | Built player on Linux — native, or the `.exe` via Proton | [`linux/binary-native.sh`](linux/binary-native.sh) · [`linux/binary-proton.sh`](linux/binary-proton.sh) · [docs](docs/02-linux-binary.md) |
| 3 | **Linux alternatives** | Vulkan / OpenGL / software, GPU vs CPU display, X11 vs Wayland | [docs](docs/03-linux-alternatives.md) |
| 4 | **Windows editor** | Editor + automation harness on Windows | [`windows/reset-and-launch-editor.ps1`](windows/reset-and-launch-editor.ps1) · [docs](docs/04-windows-editor.md) |
| 5 | **Windows binary** | Build & run the Windows player (autopilot telemetry) | [`windows/build-player.ps1`](windows/build-player.ps1) · [`windows/launch-binary.ps1`](windows/launch-binary.ps1) · [docs](docs/05-windows-binary.md) |
| 6 | **Windows VM + {editor,binary}** | A QEMU Windows guest driven entirely from the Linux host | [`vm/`](vm/) · [docs](docs/06-windows-vm.md) |
| 7 | **Mac** | Build & test the macOS universal player | [`mac/`](mac/) · [docs](docs/07-mac.md) |

Cross-cutting docs: [**shared facts**](docs/00-shared.md) (read first),
[**testing methodology**](docs/08-testing.md) (perf A/B + render correctness),
[**the autonomous loop**](docs/09-autonomous-loop.md) (scheduled self-driving QA
over the VM), [**AltTester**](docs/10-alttester.md) (drive/inspect a built
player), the [**visual-fidelity tour**](docs/11-fidelity-tour.md) (drive the
bevy-explorer scene battery without modifying it), the [**UI capture
atlas**](docs/12-ui-capture-atlas.md) (reflection driver recipes to reach every
auth/loading/UI screen), the [**bevy benchmark**](docs/13-bevy-benchmark.md)
(interleaved A/B frame-time + conformance over the bevy harness), and the
[**PR-review loop**](docs/14-pr-review-loop.md) (stack the local fix branch onto
each open upstream PR, then compile + capture + review it), the
[**batchmode conversion loop**](docs/15-batchmode-conversion.md) (drive the
asset-bundle-converter headlessly to build a reference corpus — plus the hang
watchdog / run-serialization machinery that backstops *any* long batchmode job),
and the [**in-editor telemetry modes**](docs/16-telemetry-modes.md) (the four
`-executeMethod` benchmark entry points — perf A/B, CPU breakdown, render
decomposition, shadow A/B — that *produce* the CSVs `analysis/` judges), and the
[**WASM/WebGPU measurement**](docs/17-wasm-web-measurement.md) (drive the
bevy-explorer web build in headless chromium over CDP — the deterministic
`orbit_cpu` A/B that's the only trustworthy web metric, plus the hybrid-realm +
CORS/CORP proxy for deterministic loads and a measurement-readiness gate), and
the [**built-player perf**](docs/18-builtplayer-perf.md) (the AutoPilot
Development-player capture that is the trustworthy headline perf number — why the
editor isn't, the build-staleness traps, and the fixed-crowd benchmark).
The loop mechanics live in [`loop/`](loop/) and [`pr-review/`](pr-review/).

## Repository map

```
config.sh            Single source of truth: Unity version, project paths, build
                     executeMethods, ports, auth — sourced by every shell script.
                     Everything is env-overridable; nothing machine-specific is
                     the only option.

lib/                 Shared shell libraries (sourced, not run):
  common.sh            logging, polling, scoped process kills
  headless-display.sh  nested sway+wayvnc+Xwayland headless display + audio (Linux)
  auth.sh              throwaway-wallet login (URL capture + signing)

auth/                auth-driver.py (signs the challenge) + xdg-open (URL shim)

unity/               C# to drop into Explorer/Assets/Editor/ (cross-platform):
  BuildScript.cs       one batchmode build entry point per platform
  ClaudeIPC.cs         file-based IPC to drive a running editor
  DclPlaytestHarness.cs in-editor perf/telemetry harness (reflection-based)

automation-bridge/   License-free in-player automation bridge for unity-explorer
                     — a Chrome-DevTools-Protocol-driven alternative to AltTester
                     (AutomationCore/Input/Screenshot + a CDP package + self-test).
                     APPLY.md / WIRING.md explain how to graft it into the client.

linux/   windows/   vm/   mac/    Per-target launchers (see the table).
loop/                Autonomous-loop orchestrator (run-loop.sh), state-file
                     template, and the regression-check sentinel list.
pr-review/           PR-review loop: stack the local fix branch onto each open
                     upstream PR, compile + capture the UI atlas, write a review.
  pr-review-tick.sh    one tick (pick → stack → compile → capture → review)
  pr-pick.py           rotate through open PRs (unauthenticated GitHub API)
  pr-stack.ps1         guest: fetch a PR head + cherry-pick our fixes onto it
  reset-and-batchcompile.ps1    guest: definitive batchmode compile (warms assemblies)
  launch-atlas-warm.ps1 guest: warm full-atlas capture (no cold-recompile hang)
  pr-write-review.py      render PR-<N>-review-<NN>.md + screen-health verdict
  pr-baseline.sh       measure the broken-at-baseline screen set (stack, no PR)
  pr-broken/recheck/count.py  re-verify suspected regressions (flaky vs confirmed)
convert/             Headless asset-bundle-converter batchmode loop (docs/15):
  convert-loop.sh        per-entity conversion loop (resume + tolerated fails)
  unity-batch-watchdog.sh kills a CPU-stalled batchmode Unity (any long job)
bevy/                Thin launchers that drive the bevy-explorer harness as a
                     read-only external (never modify that repo):
  fidelity-tour.sh     visual-fidelity scene gallery
  bench-ab.sh          interleaved A/B frame-time benchmark of two NATIVE binaries
  conformance.sh       scene-test battery, 🟢/🔴 count + baseline gate
  bench-summarize.py   A/B diff table (load secs, hitches, per-scene phases)
  web-bench-ab.sh      interleaved orbit_cpu A/B of two WASM bundles over CDP (docs/17)
  measure-ready.sh     GO/WAIT gate: is the host quiet enough to measure right now?
  catalyst-cors-proxy.py  add CORS/CORP to a local catalyst so a COEP wasm load works
atlas/               The UI-capture atlas's naming layer (path-free, vendored;
                     engine/drivers/PNGs live in the VM harness — see docs/12):
  atlas-codes.json     canonical route→<CODE> registry (the names)
  INDEX.md             generated index of every named UI surface
  gen-index.py         regenerate INDEX.md from the registry
  consolidate-atlas.py rename raw shots to <CODE>-<route>.png
  recipes.json         machine-readable recipe per screen (rebuild-from source)
  DIGEST.md            human-readable digest of the recipes
analysis/            perf-analyze.py (paired A/B stats) + perf-analyze-multi.py
                     (per-knob render-decomposition stats) for telemetry CSVs.
                     The CSVs are produced by vm/run-telemetry.sh (docs/16).
patches/             Optional client-retargeting patches (gateway routing, realm
                     list, comms-hold) to point a client build at your own
                     catalyst/realm — templates; set your host. See patches/README.md.
docs/                One doc per target + the cross-cutting docs (00, 08–18).
```

## Requirements

The rig is shell + Python + C# glue around tools you already have; nothing here
compiles. What each target needs:

| Need | For | Notes |
|------|-----|-------|
| **Unity 6000.4.0f1** + a license | every build/editor target | version pinned from the project; activate once via Hub (see `config.sh` `DCL_UNITY_PERSIST`) |
| A checkout of the **Unity Explorer** repo | everything | point `DCL_EXPLORER_REPO` at it; must contain `Explorer/` |
| `bash`, `python3` | all scripts | |
| `sway`, `wayvnc`, `Xwayland`, `bubblewrap`, `grim`, `foot` | Linux headless display | auto-fetched via `nix-shell -p …` when present; else must be on `PATH` |
| `eth_account`, `requests` (pip) | headless auth signing | `auth/auth-driver.py` |
| QEMU + QMP | Windows VM target | `vm/` drives a guest with no in-guest agent |
| `pwsh` (PowerShell) | Windows targets | `windows/*.ps1` |
| A NixOS/`/nix` host | optional | triggers the FHS wrap (`DCL_FHS_WRAP`) so glibc ELFs run; a no-op elsewhere |

## Quickstart

```bash
# 0. Point the rig at your checkout (or export these in your shell rc).
export DCL_EXPLORER_REPO=~/unity-explorer       # contains Explorer/
cp unity/*.cs unity/*.asmdef "$DCL_EXPLORER_REPO/Explorer/Assets/Editor/"  # one-time

# 1. Linux editor, headless, driveable over IPC:
./linux/editor-up.sh

# 2. Build + run the Linux player on the same headless display:
#    (build from the editor, then:)
./linux/binary-native.sh

# 3. Windows player build (on a Windows host or the VM):
pwsh ./windows/build-player.ps1 -Dev
```

Every script prints what it's doing and where logs land. Start with
[`docs/00-shared.md`](docs/00-shared.md) — it explains the facts the per-target
docs assume.

## Design principles

- **One fact, one place.** Unity version, project path, build methods, graphics
  flags, ports, and auth live in `config.sh` / `docs/00-shared.md`. Per-target
  files contain only what is unique to that target.
- **Env-overridable, no hardcoded hosts.** Defaults are sensible; every path is
  a variable you can override.
- **Comments explain *why*, not *what*.** Each non-obvious trick (the bwrap
  `/tmp/.X11-unix` bind, `UMU_ID=0`, the Interactive Scheduled Task, the glibc
  AVX-512 disable) carries the reason it exists, because that's the part you
  can't re-derive.
```

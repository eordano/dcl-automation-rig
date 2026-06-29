# Coverage matrix — per-capability ported / adapted / unity-only-skip rationale

This is the rationale that backs [`../README.md`](../README.md). For every one of
the ~30 Unity-rig capabilities, exactly one verdict and the grounded reason.
Verdicts:

- **PORTED** — mechanism carries over, drives bevy/catalyst read-only.
- **ADAPTED** — kept the platform-agnostic core, replaced the Unity driver with a
  CDP/wasm/cargo path.
- **PORTED-LAUNCHER / GATED** — the launcher ports, but it calls a harness that
  does not yet exist in THIS checkout; the missing piece is named.
- **UNITY-ONLY-SKIP** — intrinsically Unity-Editor / Windows-VM / IL2CPP coupled,
  no bevy-on-react analog.

The honesty spine for all "wired" claims is [`bridge-status.md`](bridge-status.md).

## Build / run targets

| Capability | Verdict | Reason |
|---|---|---|
| linux-editor | UNITY-ONLY-SKIP | Unity 6000.4 Editor over file-IPC. The analog (a driveable engine instance) is the wasm build in headless chromium — that is the **web-cdp-capture** capability, not this. |
| linux-binary | UNITY-ONLY-SKIP | IL2CPP/GE-Proton launcher. The analog is the native `decentra-bevy` binary (**native-bench-ab**) or the wasm bundle (web path); neither is this launcher. |
| windows-editor | UNITY-ONLY-SKIP | Unity-Editor + Windows-Scheduled-Task coupled. |
| windows-binary | UNITY-ONLY-SKIP | IL2CPP Windows player. |
| windows-vm | UNITY-ONLY-SKIP | QEMU-QMP control of a Windows guest. |
| vm-mouse-click-fb | UNITY-ONLY-SKIP | Framebuffer mouse-clicking a VM. The React HUD is driven by real URLs + DOM in a browser — no framebuffer clicking, no VM. |
| mac-target | UNITY-ONLY-SKIP | macOS Unity build. Perf/pixel-diff ideas are covered by the web/native bench + visual-regression capabilities. |

## Display / measurement substrate

| Capability | Verdict | Reason |
|---|---|---|
| linux-alternatives | ADAPTED → `rig/bevy` + `rig/lib` | The pixman-CPU vs GPU-gles2 sway distinction and the present-needs-a-GPU wall are exactly what the chromium-on-headless-sway wasm harness depends on. KEEP the compositor/display-API matrix doc; drop the Unity force-flags. |
| headless-display | ADAPTED → `rig/lib` | The display substrate headless chromium runs on. Require GPU `gles2` sway (not pixman) for real frame timing / non-black WebGPU capture + chromium `--ozone-platform=x11`. Keep headless-display/audio/scoped-kill/`dcl_headless_shot`/per-rig isolation; drop Unity boot tweaks. |

## Engine-driving + web measurement (`rig/bevy`)

| Capability | Verdict | Reason |
|---|---|---|
| fidelity-tour | PORTED-LAUNCHER → `rig/bevy` | Drives bevy's native `run-tour` read-only (GPU present). The wasm path verifies FUNCTIONALLY (WebGPU canvas black to grim here); settle discipline ports conceptually. GATED on bevy's `benchmark/fidelity/` absent here. Don't modify the bevy scene battery. |
| product-tour-web | PORTED → `rig/bevy` | wasm build in headless chromium against local `scene-explorer-tests`; one console run classifies broken/tick/title per scene. Caveat: functional-only; per-scene tick counts GATED on a `DCL_WASM_BENCHMARK` build absent here. |
| native-bench-ab | PORTED-LAUNCHER → `rig/bevy` | Drive bevy's benchmark primitives read-only for the native engine. GATED on bevy's `benchmark/` harness + `--benchmark*` flags not in this checkout — port the launcher, build/port the harness it calls. |
| conformance-native | PORTED-LAUNCHER → `rig/bevy` | scene-explorer-tests battery on one native binary with `--scene_log_to_console`; green/red count, fail on regression. GATED on bevy's `benchmark/`. Billboard-flake + snapshot-panic caveats. |
| web-bench-ab | ADAPTED → `rig/bevy` | The bevy-on-react measurement path (wasm `orbit_cpu` over CDP). Adapted because it hard-depends on `DCL_WASM_BENCHMARK` (WASMBENCH_RESULT) + bevy's `cdp_capture.py` — NEITHER in this checkout; the `submitFps` fallback (wrap `GPUQueue.submit`) ports and works on any WebGPU bundle today. |
| web-cdp-capture | ADAPTED → `rig/bevy` | The core browser-driving primitive; drives both the wasm engine console and the React-HUD route-walk. The rig vendors its own small CDP client since bevy's `cdp_capture.py` is absent. Wired-now for console capture; sentinel-on-WASMBENCH gated on the benchmark build. |
| catalyst-cors-proxy | PORTED → `rig/bevy` | Reverse proxy adding ACAO + `Cross-Origin-Resource-Policy: cross-origin` so the COEP-isolated wasm bundle loads catalyst content; `DCL_CATALYST_UPSTREAM` → `<your-catalyst-host>` (same upstream sites uses, `client.ts:20`). Stdlib-only. |
| measure-ready-gate | PORTED → `rig/bevy` (as-is) | GO/WAIT gate on load/core + GPU-sway presence + leftover chromium + GPU util. |

## Analysis + discipline (`rig/analysis`, `rig/docs`)

| Capability | Verdict | Reason |
|---|---|---|
| perf-analysis-stats | PORTED → `rig/analysis` | `perf-analyze.py` + `perf-analyze-multi.py` port verbatim (paired-CSV A/B: Mann-Whitney U + bootstrap CI). Consume the web orbit_cpu/submitFps CSV, the native frame-time CSV, AND a catalyst-telemetry latency A/B. |
| testing-discipline | PORTED → `rig/docs` | Interleaved-only A/B, noise floor, three-bucket anomaly classification, render-equivalence G1–G8, deterministic capture for visual regression. The governing doc. |
| telemetry-modes | UNITY-ONLY-SKIP | Production side is Unity ProfilerRecorder/AutoPilot coupled; the trustworthy-CSV role is filled by web-bench-ab + native-bench-ab (consumer perf-analyze ports separately). |
| builtplayer-perf | UNITY-ONLY-SKIP | Same — Unity built-player AutoPilot perf; covered by the bench capabilities. |

## Loops (`rig/loop`, `rig/pr-review`)

| Capability | Verdict | Reason |
|---|---|---|
| autonomous-loop | ADAPTED → `rig/loop` | The mechanism (lock+TTL, state-carry, preflight, regression-sentinel gate, fix-priority tiers, verified-only commit, never-push) ports; the session step swaps Unity-in-VM for a bevy-on-react session (web tour + HUD URL-walk + catalyst health). Production loop is paused/unverified even for Unity. |
| pr-review-loop | ADAPTED → `rig/pr-review` | Port the platform-agnostic core (pr-pick rotation + head-SHA detection + reviews-as-memory, pr-write-review, pr-codereview, confirm-before-flag, empty-cherry-pick `--skip`, measured-baseline) retargeting `DCL_REVIEW_REPO_SLUG`; REWRITE stack+compile+capture: compile = cargo check/build bevy + tsc/build sites + cargo check catalyst; capture = atlas URL-walk + web product tour. Drop `pr-stack.ps1`. |

## Asset pipeline

| Capability | Verdict | Reason |
|---|---|---|
| batchmode-conversion | UNITY-ONLY-SKIP | asset-bundle-converter is a separate pinned-Unity project; the bevy-on-react AB story is catalyst's `catalyst-abgen` / `catalyst-ab-cdn` crates. Reliability patterns are reusable lessons only. |

## Auth + bridge

| Capability | Verdict | Reason |
|---|---|---|
| throwaway-auth | ADAPTED → `rig/auth` | The SIGNING half (disposable wallet, v2/requests challenge, dcl_personal_sign, POST) ports; the CAPTURE half is rewritten for the browser (read the auth URL from DOM/console via CDP) OR the engine's `/login_guest`, `/login_previous`, `/logout` console commands (main-thread-reachable today; guest/prev login wireable now, full new-wallet signing wiring unverified here). |
| bridge-scene-read | ADAPTED → `rig/docs` + the 100-pager | The honest per-surface status is [`bridge-status.md`](bridge-status.md); inventoried so the rig never claims more than scene-read-now + the three console-command actions. |

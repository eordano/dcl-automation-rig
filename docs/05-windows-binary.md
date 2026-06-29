# Target 5 — Windows binary (build & run)

> **Status:** Not verified here. Adapted from the working VM harness; not re-run on a clean host.

## Build

```powershell
.\windows\build-player.ps1            # release
.\windows\build-player.ps1 -Dev       # development build (profiler/debugging)
.\windows\build-player.ps1 -GfxApi d3d12
```

Calls the cross-platform `BuildScript.BuildWindows64[Dev]` (see
[`00-shared.md`](00-shared.md)) in `-batchmode -nographics -quit`. Output
defaults to `<repo>/build/Windows/Decentraland.exe`; override with `-Out`.

For release signing, the upstream CI signs `Decentraland.exe` with
`sslcom/esigner-codesign` and verifies with `osslsigncode` — reproduce that step
in your own pipeline if you ship the binary; it's not needed for local runs.

## Run with telemetry (autopilot)

```powershell
.\windows\launch-binary.ps1 -Realm http://localhost:8000 -Position '0,0'
```

**Autopilot mode** (`--autopilot --csv --summary`) self-drives: logs in, waits
for `LoadingStatus.Completed`, stands at spawn ~90 s sampling CPU/GPU frame time
and power, writes the CSV + summary, then quits. No GUI interaction needed.

Like the editor harness it's launched via an **Interactive Scheduled Task** so
it gets a real graphics device — `--autopilot` also implies
`--skip-version-check`, so it won't be blocked by the min-version gate.

## Reading the results

- `player-summary.txt` — CPU/GPU percentile table (avg, 1% low, 0.1% low, max),
  a CPU-bound/GPU-bound verdict, and power draw.
- `player-perf.csv` — per-frame samples. For an A/B comparison run the dev build
  twice (baseline + change) in the *same* session and feed the CSV to
  [`analysis/perf-analyze.py`](../analysis/perf-analyze.py).
- `Player.log` — load-stage markers and `[AUTOPILOT-GROUP]` / `[BENCH]` lines.

## Native plugin (audio analysis)

The repo ships a Rust plugin built per-platform; the Windows recipe is
`native/audio-analysis/compile_win.bat` (`cargo build --release`, copy the DLL
into `Assets/Plugins/NativeAudioAnalysis/Libraries/`). Run it once before the
first build if the prebuilt DLL is stale. (macOS equivalent uses `lipo` — see
[`07-mac.md`](07-mac.md).)

## A note on running the editor-built vs the installed binary

`build-player.ps1` produces a fresh build under `build/Windows`. The installed
launcher build lives at
`…\AppData\Local\DecentralandLauncherLight\latest\Decentraland.exe` — point
`launch-binary.ps1 -Exe` at whichever you want to measure.

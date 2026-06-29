# Target 7 — Mac

> **Status:** Partly **verified here**. The GUI-editor driving + **render-verify**
> (launch → compile → Play → in-world → screenshot) was run on this host against
> the `unity-explorer-2026-06-win11` checkout, Unity **6000.4.0f1**: the world
> loaded to `Completed` and rendered a 3D Decentraland scene (avatar + plaza).
> The **build / test / native-dylib / cloud** sections below are still adapted
> from the build scripts/CI and **not** re-verified here.

Mac is the one target in this rig that can **present a GPU window** (Metal). The
Linux editor and the Proton player both reach the GUI step and then die with no
GPU presentation (see the README ledger). So Mac is where the rig actually
**proves the explorer renders** — not just builds.

## Driving the GUI editor + render-verify (verified)

```bash
export DCL_EXPLORER_REPO=~/Projects/unity-explorer/unity-explorer-2026-06-win11
./mac/editor-up.sh        # launch (or reuse) the editor, fresh log, boot watchdog
./mac/get-in-world.sh     # Play → JUMP → world loads → screenshot the rendered frame
./mac/screenshot.sh       # ad-hoc: focus Unity + screencapture (+ optional --zoom)
```

Shots land in `$DCL_MAC_SHOTS` (`~/.dcl-rig/mac-shots/`): `01-jump-screen.png`,
`02-in-world.png`. The driving primitives live in
[`lib/mac-driver.sh`](../lib/mac-driver.sh) (focus, keystroke, CGEvent click,
screenshot, log-wait) and are reusable on their own.

### The flow `get-in-world.sh` runs

1. **`editor-up.sh`** — caffeinate (keep the panel awake), clear stale
   crash/lock state, apply the boot-NRE workaround, launch
   `Unity -projectPath … -logFile $DCL_MAC_LOG`, watch the warmup window.
2. **Cmd+P** to enter Play; wait for `Current loading stage: AuthenticationScreenShowing`.
   Re-send Cmd+P once if a compile swallowed the first keystroke.
3. **Click the red "JUMP INTO DECENTRALAND" button** with a real CGEvent.
4. Wait for `Current loading stage: Completed` (it runs
   `ProfileLoading → PlayerTeleporting → LandscapeLoading → PlayerAvatarLoading →
   GlobalPXsLoading → Completed`), settle a few frames, **screenshot**.

### Why each trick exists (the part you can't re-derive)

- **Focus before keystrokes.** AppKit routes keys only to the process whose
  `frontmost` is set — plain `activate` is NOT enough. Every keystroke helper
  calls `dcl_mac_focus` first.
- **Clicks need a REAL CGEvent.** Unity's UGUI samples the hardware cursor, so an
  `osascript … click` lands on the window but is ignored by in-game UI. The
  JUMP button only fires for a CGEvent — that's [`mac/click.swift`](../mac/click.swift)
  (`swift mac/click.swift <x> <y>`). Keystrokes (Cmd+P/Cmd+R) via osascript are fine.
- **The JUMP click is the gate.** Boot parks at `AuthenticationScreenShowing` and
  proceeds only after the click. `--skip-auth-screen` only auto-fills a *cached*
  identity — it does NOT remove the click. `Curl error 42: Callback aborted` in
  the log is a red herring (it appears on successful loads too).
- **Calibrate the click.** The button sits in the Game *sub-panel*, so its window
  fraction isn't portable. Screenshot once, crop to the button (`sips`), halve the
  pixel center (2× Retina) → set `DCL_MAC_JUMP_X/Y`. This host: **1134,378**
  (window `637,33 875×949`). The fractional fallback only lands with the Game tab
  on "Maximize On Play".
- **The boot NRE bites Mac too.** Unity 6000.4's `LifecycleController` hits an
  `OrderedAssemblyList.TopologicalSort` NRE at pre-deserialization (the same one
  documented for [Linux](01-linux-editor.md)). `editor-up.sh` runs
  `linux/disable-test-asmdefs.sh` by default (`DCL_MAC_DISABLE_TEST_ASMDEFS=1`) —
  it renames the `UNITY_INCLUDE_TESTS` dirs in `Library/PackageCache` (generated
  state, never tracked source). **Observed:** the rename reduces it but doesn't
  always fully cover it; here the NRE still fired yet the editor **survived** all
  the way to a rendered world. If a boot wedges, relaunch (it's ~50%
  non-deterministic) or reach for `unity-patches` (the Mono.Cecil patch, see 01).
- **caffeinate, or black shots.** A slept Retina panel yields all-black
  `screencapture`. `dcl_mac_keepawake` runs `caffeinate -dimsu` for any capture run.
- **Watch the right log.** `editor-up.sh` pins `-logFile $DCL_MAC_LOG`
  (`~/.dcl-rig/mac-editor.log`, fresh per launch) so we don't tail the shared,
  ever-growing `~/Library/Logs/Unity/Editor.log`. Anchor reads on line numbers —
  the in-log clock can be skewed hours from system time.

### Knobs

| Var | Default | Purpose |
|-----|---------|---------|
| `DCL_MAC_LOG` | `~/.dcl-rig/mac-editor.log` | editor `-logFile` (fresh per launch) |
| `DCL_MAC_SHOTS` | `~/.dcl-rig/mac-shots` | where screenshots land |
| `DCL_MAC_JUMP_X` / `_Y` | — | absolute logical click point for JUMP (preferred) |
| `DCL_MAC_JUMP_FRAC` / `_FRAC_X` | 0.62 / 0.5 | fractional fallback (maximize-on-play) |
| `DCL_MAC_DISABLE_TEST_ASMDEFS` | 1 | apply the boot-NRE asmdef workaround |
| `DCL_MAC_APP_ARGS` | — | Explorer `--` args (e.g. `--skip-auth-screen`) |
| `DCL_MAC_WARMUP` | 180 | boot-watch window (s) |
| `DCL_MARK_AUTH` / `DCL_MARK_COMPLETED` | see `config.sh` | loading-stage markers |

## ClaudeIPC, fidelity tour & UI atlas

> **Status:** ClaudeIPC round-trip, the **fidelity tour** (`TeleportTo` +
> `screenshot-game`), and the **clean-HUD** defaults (debug panel + rewards popup
> hidden) were run and verified here. `atlas-capture.sh`'s IPC wiring + output-path
> override are verified; the per-screen force-show *drivers* are adapted from the
> VM harness and need per-run verification (docs/12 tiers).

Mac is the **first host to actually run ClaudeIPC** (the Linux editor can't
present, so its IPC path was never exercised). Drop the harness in (dedicated
sub-folder — see below), and the in-editor [`ClaudeIPC.cs`](../unity/ClaudeIPC.cs)
exposes `exec` (any static method), `world-ready`, `ui-list`/`ui-click` (drive UI
by button label), and `screenshot-game` (renders `Camera.main` to a PNG).
[`lib/mac-ipc.sh`](../lib/mac-ipc.sh) is the shell client.

### Wiring it (one-time, modifies the checkout)

```bash
DEST="$DCL_PROJECT_DIR/Assets/Editor/DclHarness"        # a DEDICATED sub-folder
mkdir -p "$DEST" && cp unity/*.cs unity/*.asmdef "$DEST/"
```

The asmdef scopes its whole folder tree — at `Assets/Editor/` root it would pull
the project's own editor scripts into this reference-less assembly and break their
compile. Then recompile (Cmd+R / `dcl_mac_recompile`); the log shows
`[ClaudeIPC] watcher armed`. The Unity-6 boot NRE is pre-existing and survivable.

### Fidelity tour

```bash
./mac/fidelity-tour.sh                      # default stops → $DCL_MAC_SHOTS/tour
DCL_TOUR_STOPS="0,0 -9,-9 74,-9" ./mac/fidelity-tour.sh
```

Gets in-world, then per stop: `exec TeleportTo x,y` → floor + `world-ready`
re-check → `screenshot-game`. `screenshot-game` bypasses the screen-space HUD, so
the gallery is clean 3D. Ported the settle discipline from [docs/11](11-fidelity-tour.md)
(floor after teleport, real readiness check, per-stop timeout).

## Remote-trigger runner (poll → tour → publish)

[`mac/atlas-runner.sh`](../mac/atlas-runner.sh) is the Mac analogue of
[`loop/run-loop.sh`](../loop/run-loop.sh): one idempotent tick — lock(TTL) → check
trigger → run `atlas-capture.sh` (the no-relaunch IPC tour, **in the logged-in GUI
session** — the only place that presents Metal) → publish the consolidated gallery
→ record state → retention → release. A user LaunchAgent
([`mac/com.dcl.atlas-runner.plist`](../mac/com.dcl.atlas-runner.plist)) invokes it on
an interval, so the interval is the poll cadence.

```bash
# install (reversible) — runs in the Aqua session so it can render:
cp mac/com.dcl.atlas-runner.plist ~/Library/LaunchAgents/
launchctl load   ~/Library/LaunchAgents/com.dcl.atlas-runner.plist
# pause/uninstall:
launchctl unload ~/Library/LaunchAgents/com.dcl.atlas-runner.plist   # (+ rm to remove)

# fire a capture remotely WITHOUT pushing — touch the trigger, or move a watched
# local branch tip. The PR loop triggers Mac captures the same way:
touch ~/.dcl-rig/atlas-trigger
```

Galleries publish to `$DCL_RUNNER_PUBLISH` (default `~/.dcl-rig/mac-shots/published/<TS>/`
+ `latest/`; set it to a `user@host:path` rsync target to ship off-box). Knobs:
`DCL_RUNNER_TRIGGER_FILE` / `_BRANCH` / `_REPO`, `DCL_RUNNER_MODE` (atlas|auth),
`DCL_RUNNER_PUBLISH`, `DCL_RUNNER_KEEP` — all in [`config.sh`](../config.sh).
Reversible by design: edits no source, never commits/pushes, writes only under `~/.dcl-rig`.

### Clean captures by default

`get-in-world.sh` and `fidelity-tour.sh` call `dcl_ipc_clean_hud` once in-world,
which execs `HideDebugPanel()` (the right-edge dev panel) and `HideRewardsPopup()`
(the `NewNotificationPanel` toast/rewards popup) so captures aren't cluttered.
(The recurring `SuperScrollView.LoopListView2` NRE in the log is the **explorer's
own** UI package, not the rig — independent of these hides.)

### UI atlas

```bash
./mac/atlas-capture.sh auth     # pre-world auth/OTP/web3/lobby screens
./mac/atlas-capture.sh atlas    # in-world UI surfaces (quiet parcel 140,140)
```

Relaunches the editor with `DCL_HARNESS_SHOTS`/`DCL_HARNESS_REPORT` pointed at Mac
paths (the harness reads them at launch — the Mac-parity edit made the once-Windows
paths env-overridable), execs `RunAtlasFromMenu` / `RunAuthCaptureFromMenu`, waits
for the report, then `atlas/consolidate-atlas.py` renames raw shots to
`<CODE>-<route>.png`. The native CaptureShot path composites screen-space UI
(unlike `screenshot-game`), which is why UI capture goes through the harness.

### IPC knobs

| Var | Default | Purpose |
|-----|---------|---------|
| `DCL_HARNESS_SHOTS` / `DCL_HARNESS_REPORT` | under `$DCL_MAC_SHOTS` | harness capture output (read at editor launch) |
| `DCL_TOUR_STOPS` | `0,0 -9,-9 74,-9` | tour parcels |
| `DCL_TOUR_FLOOR` | 12 | settle floor (s) after each teleport |
| `DCL_JUMP_LABEL` | `JUMP INTO DECENTRALAND` | button label for IPC ui-click |

## Build

```bash
export DCL_EXPLORER_REPO=~/unity-explorer
./mac/build-player.sh           # release
./mac/build-player.sh --dev     # development build
DCL_GFX_API=metal ./mac/build-player.sh
```

Calls `BuildScript.BuildMacUniversal[Dev]` with `-buildTarget OSXUniversal`.
Unity emits a **universal `Decentraland.app`** (x86_64 + arm64) when the macOS
target architecture is "Intel + Apple Silicon" (the project default). Output:
`build/Mac/Decentraland.app`.

`dcl_unity_bin` resolves the editor at
`/Applications/Unity/Hub/Editor/<version>/Unity.app/Contents/MacOS/Unity` and
also the Hub's alternate `/Applications/Unity/Unity-<version>/Unity.app` layout
(what this host uses); override with `DCL_UNITY_BIN`.

## Tests

```bash
./mac/run-tests.sh editmode
./mac/run-tests.sh playmode DCL.Tests.CodeConventionsTests
```

Same CI flag set as every other platform (see [`00-shared.md`](00-shared.md)).
`-nographics` is applied for EditMode only — PlayMode needs a graphics device.

## Native plugin (universal dylib)

The Rust audio plugin is built universal with `lipo`:

```bash
cd "$DCL_EXPLORER_REPO/native/audio-analysis"
cargo build --release --target x86_64-apple-darwin
cargo build --release --target aarch64-apple-darwin
lipo -create -output \
  ../../Explorer/Assets/Plugins/NativeAudioAnalysis/Libraries/audio-analysis.dylib \
  target/x86_64-apple-darwin/release/libaudio_analysis.dylib \
  target/aarch64-apple-darwin/release/libaudio_analysis.dylib
```

(This is `native/audio-analysis/compile_mac.sh` in the repo — run it once if the
prebuilt dylib is stale before the first build.)

## Running the built app

```bash
open build/Mac/Decentraland.app --args \
  --realm http://localhost:8000 --position 0,0 -force-metal -logFile ~/player.log
# autopilot telemetry run:
open build/Mac/Decentraland.app --args --autopilot --csv ~/perf.csv --summary ~/sum.txt
```

## Headless on macOS

There's no sway/VNC equivalent here. macOS CI typically runs the build/test
batchmode jobs (which need no display) on a real or virtualized Mac; for *runtime*
telemetry use a machine with a session attached (the `.app` needs a window
server). Autopilot mode then does the driving, same as Windows. For the editor,
the GUI-driving flow above needs a logged-in session (a real screen, or one held
awake with `caffeinate`).

## Notes from the build pipeline

- Preserve permissions when moving the `.app` around — `tar` it
  (`tar -cvf build.tar build`) rather than zipping, or the `Contents/MacOS/Explorer`
  executable bit is lost. The cloud pipeline checks `os.access(..., X_OK)` after
  download for exactly this reason.
- Exclude `*_BackUpThisFolder_ButDontShipItWithYourGame` from anything you ship.

## Known issue: wearables render white — abgen BC7→ASTC converter gap (NOT the client)

Status: **confirmed 2026-06-22 by isolation test.** White-clothes is in **OUR abgen ABs
(the per-platform texture converter), not the client.** Fix lives in the catalyst/abgen
lane (BC7→ASTC transcode for `_mac`), **not** in the unity-explorer client.

> CORRECTION: an earlier revision of this note blamed the **Mac/Metal client**
> (`scene_ignore_mac` not fetched / shader-dependency load path). That was **wrong** — it
> was an inference from logs. The `scene_ignore` "not fetched" signal was a red herring
> (it ships locally in StreamingAssets), and the direct render test below disproves the
> client theory. Do not re-chase the client.

Symptom: avatar clothing wearables render **flat white** (e.g. `black_jacket` → RGB
~205,205,205, the untextured-material default), while hair/skin and non-clothing
textures render with correct color.

Root cause (proven): the **abgen converter drops/mis-transcodes the clothing wearable
textures for the `_mac` platform** (BC7 authored, ASTC needed for Metal) → no texture →
white. It is **content-addressed in the AB**, so it reproduces on any client.

Isolation proof — a **pristine, unmodified upstream `dev` client also renders our ABs
white**:
- Ran upstream `dev` (no consolidation/fork changes) + 2 config-only patches (URL routing:
  `CATALYST_BASE` gateway + `myrealm` realm) + realm `myrealm`, loading our catalyst ABs.
- 18 base-avatar wearables loaded; `black_jacket` rendered **white (205,205,205)** while
  `hair_punk` rendered **colored (pink)** and skin colored → the client CAN texture
  wearables; the **clothing AB texture specifically fails** → converter, not client.
- Evidence: `~/catalyst-atlas/isolation-PRISTINEDEV-whitehoodie.png`.
- An unmodified client cannot render our ABs correctly ⇒ the defect is in the ABs.

Quick repro / check:
```bash
# render against catalyst (myrealm realm), open the Backpack, sample a CLOTHING wearable:
#   colored hair/skin + flat-white jacket/hoodie/pants == the converter gap present.
# server side is fully correct (ab-cdn serves 200, manifests v0-abgen) — do NOT debug client.
```
Atlas gallery impact: the Mac column ships **flagged** — UI + world shots valid;
`E06/E07/E08` + avatar flagged white (see `mac-column-manifest.{md,json}`). The flag is
a known **AB/converter** gap, identical on Windows-vs-Mac only in that each needs its
platform-correct texture transcode.

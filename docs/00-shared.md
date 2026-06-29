# Shared facts (read this first)

> **Status:** Mixed. The headless-display and config facts are verified (see README ledger); the build/run/test commands are adapted from working rigs and NOT re-verified on a clean host.

Everything every target assumes. The per-target docs build on this and don't
repeat it. The machine-readable version of all of this is [`config.sh`](../config.sh).

## The project

- **Engine:** Unity **6000.4.0f1** (pinned in `Explorer/ProjectSettings/ProjectVersion.txt`;
  scripts read it from there so it can't drift). Scripting backend: **Mono2x**.
- **Render pipeline:** URP, Forward path.
- **Project dir:** `Explorer/` inside the repo (`$DCL_PROJECT_DIR`). The built
  scene is `Assets/Scenes/Main.unity`.
- **Platforms:** StandaloneWindows64, StandaloneOSX (universal x86_64+arm64),
  StandaloneLinux64.

## Build entry points (the same on every platform)

Drop [`unity/BuildScript.cs`](../unity/BuildScript.cs) into
`Explorer/Assets/Editor/`. It exposes one `-executeMethod` target per
platform × (release | dev):

| Method | Target |
|--------|--------|
| `BuildScript.BuildWindows64` / `…Dev` | Windows player |
| `BuildScript.BuildLinux64` / `…Dev` | Linux player |
| `BuildScript.BuildMacUniversal` / `…Dev` | macOS universal `.app` |

It reads `DCL_BUILD_OUT`, `DCL_BUILD_VERSION`, and optional `DCL_GFX_API` from
the environment, and exits `0` success / `1` build-failed / `3` exception so CI
can branch on the code.

### The canonical batchmode invocation

Every build/test boils down to this shape (paths differ per platform — see
`dcl_unity_bin` in `config.sh`):

```
<Unity> -batchmode -nographics -quit \
        -projectPath <Explorer> \
        -executeMethod <BuildScript.Build…> \
        -buildTarget <Win64|OSXUniversal|Linux64> \
        -logFile <log>
```

Key flags:

| Flag | Meaning |
|------|---------|
| `-batchmode` | no GUI, exit when done |
| `-nographics` | no graphics device — **builds & EditMode tests only**. *Never* for PlayMode or for the in-editor harness (they need rendering). |
| `-quit` | quit after `-executeMethod` returns |
| `-executeMethod` | static method to run (build, test, or harness) |
| `-buildTarget` | the platform to build for |
| `-logFile` | redirect the editor log (use a file; `-` = stdout) |

### Tests

`make test-editmode` / `make test-playmode` in the repo, or the platform
wrappers (`windows/run-tests.ps1`, `mac/run-tests.sh`). All use the same CI flag
set: `-runTests -testPlatform … -testCategory "!Performance"
-burst-disable-compilation -accept-apiupdate`.

## Graphics APIs

Unity auto-selects the platform-native API unless you override it. Two override
points:

- **At build time:** set `DCL_GFX_API` (`vulkan|gl|d3d11|d3d12|metal`) — pins it
  into PlayerSettings via `BuildScript.cs`.
- **At run time:** pass a `-force-*` flag to the player/editor:

| Platform | Default | Force flags |
|----------|---------|-------------|
| Windows | D3D11 | `-force-d3d11` `-force-d3d12` `-force-vulkan` `-force-glcore` |
| Linux | Vulkan | `-force-vulkan` `-force-glcore` |
| macOS | Metal | `-force-metal` `-force-glcore` |

See [`03-linux-alternatives.md`](03-linux-alternatives.md) for when each matters.

## Auth (headless login)

The editor and the player both authenticate by calling
`Application.OpenURL(<auth-url>)` and waiting for an out-of-band wallet
signature. Headless, the rig substitutes a robot:

1. **Capture** the URL — `auth/xdg-open` shadows the real one and logs every
   `OpenURL()` to `$DCL_URL_LOG`.
2. **Sign** it — `auth/auth-driver.py` reads that log, fetches the challenge
   from `auth-api.decentraland.org`, signs with a disposable wallet
   (`$DCL_WALLET_FILE`), and POSTs the signature back.

`lib/auth.sh` wires both together. On Linux/Wine the shim must also be set in
Wine's `WineBrowser` registry key (handled by `linux/binary-proton.sh`).

## License activation

- **Local editor (any OS):** activate once via Unity Hub; the license persists.
- **CI / Docker:** GameCI images activate from `UNITY_EMAIL` / `UNITY_PASSWORD`
  / `UNITY_LICENSE` (personal `.ulf`) env vars.
- **Cloud builds:** Unity Cloud Build holds the license server-side; the repo's
  `scripts/cloudbuild/build.py` drives it and passes `PARAM_*` env vars into the
  `CloudBuild.PreExport` hook.

## Run-time player flags (all platforms)

Unity engine flags (lowercase, single dash):

```
-screen-width N -screen-height N -screen-fullscreen 0|1
-force-vulkan | -force-glcore | -force-d3d11 | -force-metal
-logFile <path>
```

Explorer app arguments (double dash). The full reference is the project's
`docs/app-arguments.md`; the ones the rig leans on:

```
--realm=<url> --position x,y          # point at a realm / spawn parcel
--graphics high|medium|low            # quality preset
--debug                               # enable debug-gated flags (some below need this)
--skip-version-check                  # bypass the min-version gate (implied by --autopilot)
--skip-auth-screen / --skip-minimum-specs-screen
--local-scene true                    # local scene development (pair with --realm)
--autopilot --csv <f> --summary <f>   # self-driving telemetry run, then quit
--alttester                           # load AltTester instrumentation (see 10)
```

### Deterministic capture bundle (visual regression)

To make two runs pixel-comparable, strip every source of frame-to-frame
variation (requires `--debug` for the gated flags):

```
--landscape-terrain-enabled false     # identical empty/grid background
--skybox-time-enabled false           # freeze time-of-day → constant lighting/shadows
--resolution 1024x768                 # fixed render res (match your reference frames; fullscreen only)
--disable-hud                         # hide chat/minimap/notifications (scene UI stays)
--skip-minimum-specs-screen           # don't let a low-spec preset override --graphics
```

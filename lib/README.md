# rig/lib — the display + process substrate every web capability runs on

This is the web-stack port of the Unity rig's `lib/` (headless display + common
helpers). It carries the headless-display capability and the display half of
the **linux-alternatives** capability — the compositor/graphics-API matrix —
retargeted from "give Unity/Wine a surface" to "give headless chromium a GPU
surface the wasm engine and the React HUD can present onto."

## Files

| File | Verdict | What |
|---|---|---|
| `common.sh` | **PORTED** | `dcl_log` / `dcl_die` / `dcl_wait_for` / **`dcl_pkill_scoped`** (the per-rig isolation safety property). Verbatim — these were never Unity-specific. The one Unity boot-tweak helper was dropped. |
| `headless-display.sh` | **ADAPTED** | per-rig isolated nested sway + wayvnc + forced Xwayland + bwrap `/tmp/.X11-unix` bind + PipeWire/pulse + `dcl_headless_shot`. The load-bearing change: **require GPU `gles2`, not `pixman`.** |
| `chromium-launch.sh` | **NEW** | the chromium-onto-headless-sway launcher (`--ozone-platform=x11` + WebGPU/SAB flags). The Unity rig had no browser launcher; the web rig spawns one per capability and drives it over CDP. |
| `sway-session.conf` | **ADAPTED** | minimal nested-sway config; chromium subject instead of Unity/Wine. |

## The compositor / graphics-API matrix (linux-alternatives, display half)

Two independent axes, one option from each:

```
   COMPOSITOR / DISPLAY                CLIENT (chromium WebGPU)
   ───────────────────                ────────────────────────
   sway headless + gles2 (GPU)   ×    --enable-unsafe-webgpu  (Vulkan backend)
   sway headless + vulkan (GPU)       --ozone-platform=x11    (present via DRI3)
   sway headless + pixman (CPU)       (software WebGPU = SwiftShader, if present)
   a real X / Wayland display
   Xvfb (X11 only, no VNC)
```

### Why pixman is wrong here (the inversion vs the Unity rig)

The Unity rig **defaulted to pixman** (pure CPU; runs on any box including
containers) because Unity's load-bearing headless work — boot, license, compile,
layout — is non-GUI, and pixman was enough. The web rig **inverts that default**:

- **Presentation needs a GPU.** A Vulkan/GL *device* is not the same as the
  ability to *present a window*. On a pixman/software compositor with no DRI3,
  the WebGPU `<canvas>` captures **black** (CDP `captureScreenshot` and `grim`
  both) — exactly the wall the Unity editor/Proton player hit (`MESA: vulkan:
  No DRI3 support detected — required for presentation`). So a real, non-black
  WebGPU capture needs a **GPU-backed sway with DRI3** (`DCL_WLR_RENDERER=gles2`
  on a free GPU).
- **Frame timing needs a GPU.** pixman holds swapchain buffers for tens of ms
  and paces every client to a low frame rate — fine for correctness/driving,
  useless for measuring frame time. A perf A/B on pixman measures the
  compositor, not the engine.

`config.sh` therefore defaults `DCL_WLR_RENDERER=gles2`, and
`headless-display.sh` warns loudly if it's set to `pixman` so a measurement is
never silently taken on the wrong compositor.

### Software fallbacks (functional only)

With no GPU at all you can still drive the bundle *functionally* (DOM overlays,
console capture, scene-load classification) but NOT capture the 3D canvas or
measure frame time:

- **lavapipe** (CPU Vulkan ICD): `VK_ICD_FILENAMES=/path/lvp_icd.x86_64.json` —
  WebGPU may init a device but presentation still fails without DRI3.
- **llvmpipe** (CPU GL) / chromium **SwiftShader**: software WebGPU; slow, and
  the canvas still won't survive a grim/CDP screenshot on pixman.

This is the same "present-needs-a-GPU wall" the Unity targets documented — see
[`docs/08-testing-discipline.md`](../docs/08-testing-discipline.md) for how it
feeds the measurement methodology (GPU compositor mandatory for any frame
timing).

## Usage

```bash
. config.sh; . lib/common.sh; . lib/headless-display.sh
dcl_headless_up                         # nested gles2 sway + wayvnc + audio on $DCL_RIG_PORT
lib/chromium-launch.sh url "$DCL_HUD_BASE/?panel=settings"   # drive a HUD route
dcl_headless_shot /tmp/shot.png         # capture (DOM overlays OK; canvas needs GPU)
dcl_headless_down
```

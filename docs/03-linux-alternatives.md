# Target 3 — Linux alternatives (graphics API & display backends)

> **Status:** Partly verified. Headless display + screenshot are verified; the graphics-API and backend specifics are adapted and not all exercised here.

The Linux editor/binary targets default to a **software-rendered, CPU
compositor with Vulkan clients**, which runs anywhere. This doc is the menu of
swaps when that default is wrong for your job.

## Two independent axes

You pick one option from each:

```
   COMPOSITOR / DISPLAY                CLIENT GRAPHICS API
   ───────────────────                ───────────────────
   sway headless + pixman (CPU)   ×   -force-vulkan   (default)
   sway headless + GPU (gles2)        -force-glcore   (OpenGL)
   a real X / Wayland display         -force-d3d11    (Wine/DXVK only)
   Xvfb (X11 only, no VNC)
```

## Compositor / display backends

### sway headless + pixman — the default
`WLR_BACKENDS=headless WLR_RENDERER=pixman`. Pure CPU; no GPU needed; works on
any box including containers. **Downside for perf work:** pixman holds swapchain
buffers for tens of milliseconds and paces every client to a low frame rate —
fine for correctness and driving, useless for measuring frame time.

### sway headless + GPU
Set `DCL_WLR_RENDERER=gles2` (or `vulkan`) when the box has a usable GPU and you
need real frame timing. A dedicated perf rig runs a *separate* GPU sway at high
refresh precisely so the compositor never bottlenecks the client. Use
this for any benchmark — see [`08-testing.md`](08-testing.md).

### A real display
If you already have an X or Wayland session, skip the nested sway entirely:
export `DISPLAY`/`WAYLAND_DISPLAY` and run the editor/player directly. You lose
the VNC export and the per-rig isolation, but it's simplest for desktop debugging.

### Xvfb
A headless **X11-only** server (`Xvfb :99 -screen 0 1920x1080x24`). No
compositor, no Wayland, no VNC — but dead simple for pure-batch screenshot jobs
where you don't need to *watch* it. Unity and Wine are happy with it. Capture
with `xwd`/ImageMagick instead of grim.

## Client graphics APIs

| Flag | When to use |
|------|-------------|
| `-force-vulkan` | Default. Best perf; needs a working Vulkan ICD (Mesa lavapipe works CPU-only). |
| `-force-glcore` | Fallback when Vulkan init fails, or to reproduce GL-specific bugs. |
| `-force-d3d11` | Only meaningful for the Wine/Proton player (DXVK maps D3D11→Vulkan). |

Pin at **run time** with the flag, or at **build time** with `DCL_GFX_API` (see
[`00-shared.md`](00-shared.md)).

### Software-only boxes
With no GPU at all, install Mesa's software stack and point the loader at it:

```bash
# Vulkan via lavapipe (CPU):
VK_ICD_FILENAMES=/path/to/lvp_icd.x86_64.json ./linux/binary-native.sh
# or OpenGL via llvmpipe:
LIBGL_ALWAYS_SOFTWARE=1 DCL_GFX_FLAG=-force-glcore ./linux/binary-native.sh
```

## Presentation needs a GPU (verified the hard way)

A Vulkan/GL **device** is not the same as the ability to **present a window**.
On a host with no GPU available for presentation, the editor and the Proton
player both reach the GUI step and then can't present onto the pixman-backed
Xwayland:

- **Vulkan** inits a device fine, then fails at the swapchain:
  `MESA: vulkan: No DRI3 support detected — required for presentation` → the app exits.
- **`-force-glcore`** fails *earlier* — no GLX on the software Xwayland — so it's
  not a reliable fallback here either.

So `-force-glcore` is the right move when **Vulkan device init** fails, but it
will *not* rescue a missing-presentation host. To actually present a 3D window
headless you need a **GPU-backed sway with DRI3** (`DCL_WLR_RENDERER=gles2` on a
free GPU), not the CPU/pixman compositor. CPU/pixman is fine for the editor's
non-GUI work (boot, license, compile) — it just can't show the window.

## Renderer gotcha worth knowing

On Mesa, a degenerate (zero-scale / singular-matrix) transform can rasterize as
a garbage triangle where other clients silently clamp it. If something renders
as noise *only* on the Linux build, suspect this before assuming a content bug —
and confirm with the three-bucket method in [`08-testing.md`](08-testing.md).

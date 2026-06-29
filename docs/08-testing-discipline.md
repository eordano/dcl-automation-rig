# Testing discipline — perf & correctness methodology (renderer-agnostic)

> **Status:** PORTED from the Unity rig's docs/08. The methodology is platform-
> independent; the harness that produces the data is web-stack (web orbit/submitFps,
> native frame-time, catalyst latency). `rig/analysis/perf-analyze*.py` are
> verified on synthetic data; the rest is adapted, not re-run end-to-end here.

The hard-won discipline behind trustworthy measurements. The *producer* is
platform-specific (the wasm CDP capture, the native bevy harness, a catalyst
latency probe); the *method* is not.

## Performance

### The one rule: only interleaved A/B within a single session is trustworthy

Never compare a run today against a run yesterday — between-session variance
(machine load, thermals, driver state) dwarfs most real deltas. Instead:

- Build **both** conditions, alternate them `a,b,a,b…` (or counterbalanced ABBA)
  **inside one session**, holding a lock so nothing else competes. This is what
  `rig/bevy/bench-ab.sh` (native), `rig/bevy/web-bench-ab.sh` (wasm), and the
  catalyst latency probe (`rig/analysis/catalyst-telemetry-ab.md`) all do.
- The **relative** delta survives shared load (both sides see the same
  contention); only absolute numbers inflate.
- Collapse each A/B window to its **median** (per-frame CPU is autocorrelated and
  GC-heavy — frames are not iid), then compare the window medians.
- Report the **whole shape** (p25/p50/p75/p90/p99), not just the mean — a change
  can be median-neutral but tail-heavy. Headline metric: **pooled p90**.

`rig/analysis/perf-analyze.py` implements exactly this on the paired CSV: window
medians → Mann-Whitney U → bootstrap 95% CI on `median(A) − median(B)`. **A delta
is real only when the CI excludes 0.** Pure stdlib.

### Measurement hygiene (web-stack specifics)

- **Use a GPU compositor for frame timing.** A pixman (CPU) sway paces all
  clients to a low frame rate and the WebGPU canvas captures black — meaningless
  for perf and impossible to screenshot. Run a GPU `gles2` sway (config default;
  see [`../lib/README.md`](../lib/README.md)).
- **Web metrics are submitFps-only today.** The deterministic `orbit_cpu` + the
  per-scene tick counts need a `DCL_WASM_BENCHMARK` build absent here
  ([`../bevy/BUILD-WASM-BENCHMARK.md`](../bevy/BUILD-WASM-BENCHMARK.md)). Until it
  lands, the trustworthy web number is the `submitFps` proxy (`GPUQueue.submit`
  wrap). Don't present a free-running fps on a live scene as a few-percent verdict
  — it can't resolve it.
- **Pin cores and gate on load** on a shared box (`taskset`, and wait until
  `/proc/loadavg` is low — `rig/bevy/measure-ready.sh` is the gate). Even pinned,
  a heavy co-tenant widens the noise floor.
- **Establish the noise floor first** — A/B two builds of the *same* commit; any
  delta smaller than that floor is "no regression", never a fabricated number.
- **Separate steady-state from crowd wins.** Some changes only move with N
  synthetic players injected (`--benchmark_fake_players`, native); don't report a
  crowd win as an absolute steady-state frame delta.

## Correctness / rendering

### Classify every anomaly into exactly one bucket — and don't conclude solo

1. **Fork regression** — the fork diverged from upstream on the code path.
   *Provable only* by `git diff upstream/main..main -- <path>` showing a real
   logic change. (Vendored copies show as all-additions noise — diff against the
   upstream mirror, LF-normalized, to find real edits.)
2. **Scene content** — the scene is authored that way. Provable by reading the
   deployed entity (`/content/entities/active`) and its GLBs.
3. **Platform limitation** — the asset/codec/feature degrades the same way on
   *any* wasm/WebGPU (or native Linux) client. Provable from the platform's own
   failure logs (a wgpu-validation error, a missing codec, a `closure invoked
   recursively` rejection).

Cross-check each anomaly with **≥3 independent agents/framings** before recording
a verdict — a single confident pass landed wrong more than once. Fix only
confirmed bucket-1 regressions; file 2 and 3 as known-not-a-bug.

### Ruling out content (the GLB-parse check)

Bucket 2 is settled without a client: pull the deployed entity and parse the GLB
material table straight from its JSON chunk. A **material-less primitive**
(`primitives[].material == null`, `materials: []`) is the tell that the scene
colours/textures it at *runtime* via the SDK — so a flat colour there is content,
not a missing reference.

```python
import struct, json, urllib.request
# h = a content hash from POST /content/entities/active {"pointers":["x,y"]}
d = urllib.request.urlopen(f"http://127.0.0.1:5142/content/contents/{h}").read()  # via catalyst-cors-proxy
assert d[:4] == b'glTF'
clen = struct.unpack('<I', d[12:16])[0]
j = json.loads(d[20:20+clen])
# j["materials"][i]["pbrMetallicRoughness"]["baseColorFactor" | "baseColorTexture"]
```

### Render equivalence, not pixel equality

When comparing renders (e.g. catalyst AB-gen parity, or before/after a change),
judge by an **alpha-weighted perceptual mean**, never the worst single texel — a
255-off texel under a transparent pixel is invisible. The render-equivalence
taxonomy grades a divergence into one of eight tiers:

- **G1** byte-identical · **G2** decode-identical (bytes differ, pixels same) ·
  **G3** imperceptible (<~½ level avg) · **G3b** marginal (~½–2 levels) ·
  **G4** non-texture value noise (mesh/anim, no texture) · **G5** sampler-state
  divergence (format/colour-space/wrap/filter/mip) · **G6** visible (>~2 levels
  avg or real alpha divergence) · **G7** structural/binding (renders broken) ·
  **G8** undecodable.

G1–G3 are render-equivalent; **G5–G8 are the short list to investigate.** Always
compare against a **control** in the same session (a base-vs-base diff is the
floor), not an absolute.

### Triage: defect vs quirk vs wall

When a divergence is real, sort it into exactly one — only the first is worth
chasing:

1. **Fixable defect** — our side is wrong. Derive the rule from the reference,
   fix it, and gate **zero-regression** (every previously-matching output must
   still match).
2. **Engine quirk** — the engine does something unusual and *matching it* is
   correct (e.g. dual-bound color+normal textures swizzled; EXIF orientation
   ignored). Source-referee confirms our output matches the engine, not the source.
3. **Irreducible wall** — confirmed not derivable in a clean room (BC7 encoder
   float-order, mesh tangent f32 op-order). Documented; don't re-drill.

### Visual regression (the deterministic-capture rule)

Capture deterministic frames (freeze time-of-day, settle past dithering) from the
base build twice → that's the noise floor. A frame from the change only indicates
a real difference when it exceeds that base-vs-base floor. SSIM / PSNR + heatmaps
localize it.

> web-stack caveat: deterministic *pixel* capture of the WebGPU canvas needs a real
> GPU present path (the canvas is black on pixman/no-DRI3 here). On this host the
> web regression check is **functional** (the scene scripts loaded + ran, from the
> console) until a presenting GPU is available. DOM-overlay surfaces (the React
> HUD) DO survive a grim/CDP screenshot, so visual-regression of the HUD routes is
> available via `rig/atlas/url-walk.sh`.

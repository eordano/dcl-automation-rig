# Cross-cutting — testing methodology (perf & correctness)

> **Status:** Partly verified. `analysis/perf-analyze.py` and `analysis/perf-analyze-multi.py` are verified on synthetic data; the perf/correctness methodology is adapted from the testing playbooks, not re-run here.

The hard-won discipline behind trustworthy measurements. Applies to every
target; the harness that produces the data is platform-specific (the editor
harness, autopilot mode), the *method* is not.

## Performance

### The one rule: only interleaved A/B within a single session is trustworthy

Never compare a run today against a run yesterday — between-session variance
(machine load, thermals, driver state) dwarfs most real deltas. Instead:

- Build **both** binaries/conditions, alternate them `a,b,a,b…` (or counterbalanced
  ABBA) **inside one session**, holding a lock so nothing else competes.
- The **relative** delta survives shared load (both sides see the same
  contention); only absolute numbers inflate.
- Collapse each A/B window to its **median** (per-frame CPU is autocorrelated and
  GC-heavy — frames are not iid), then compare the window medians.
- Report the **whole shape** (p25/p50/p75/p90/p99), not just the mean — a change
  can be median-neutral but tail-heavy. Headline metric: **pooled p90**.

[`analysis/perf-analyze.py`](../analysis/perf-analyze.py) implements exactly
this for the harness's paired CSV: window medians → Mann-Whitney U for
significance → bootstrap 95% CI on `median(A) − median(B)`. A delta is real only
when the CI excludes 0. Pure stdlib, no numpy.

```bash
analysis/perf-analyze.py harness-perf.csv          # from RunPerfHeadless / autopilot A/B
```

### Measurement hygiene

- **The built player is the perf oracle, not the editor.** The editor is
  single-threaded and session-age-confounded — its frame time drifts with how
  long it's been open and never carries the real main/render-thread split. Take
  headline numbers from a built **Development** player (real threads, real GPU
  counters) driven by AutoPilot — see [18](18-builtplayer-perf.md). The in-editor
  telemetry modes ([16](16-telemetry-modes.md)) are for *decomposition* (where
  does the frame go), not for the A/B headline.
- **Use a GPU compositor for frame timing.** The default pixman (CPU) sway paces
  all clients to a low frame rate — meaningless for perf. Run a GPU sway (see
  [`03`](03-linux-alternatives.md)). The Linux Unity player is CPU-bound at 720p
  even on a big GPU; the frame floor is ECS-executor scheduling, not GPU work.
- **Pin cores and gate on load** on a shared box (`taskset`, and wait until
  `/proc/loadavg` is low). Even pinned, a heavy co-tenant widens the noise floor.
- **Establish the noise floor first** — A/B two builds of the *same* commit; any
  delta smaller than that floor is "no regression", never a fabricated number.
- **Separate steady-state from crowd wins.** Some changes only move the needle
  with N synthetic players injected; don't report a crowd/broadcast win as an
  absolute steady-state "−X% frame".
- **Beware stale builds.** Verify the built DLL's mtime is newer than your source
  edit before trusting a run (and remember .NET string literals live in the
  UTF-16LE `#US` heap if you grep an assembly to confirm a change shipped).

## Correctness / rendering

### Classify every anomaly into exactly one bucket — and don't conclude solo

1. **Fork regression** — the fork diverged from upstream on the code path.
   *Provable only* by `git diff upstream/dev..dev -- <path>` showing a real logic
   change. (Vendored UPM copies show as all-additions noise — diff against the
   upstream mirror, LF-normalized, to find real edits.)
2. **Scene content** — the scene is authored that way. Provable by reading the
   deployed entity (`/content/entities/active`) and its GLBs.
3. **Platform limitation** — the asset/codec/feature degrades the same way on
   *any* Linux/Proton client. Provable from the platform's own failure logs
   (missing DLL, codec, Mesa singular-matrix garbage).

Cross-check each anomaly with **≥3 independent agents/framings** before recording
a verdict — a single confident pass landed wrong more than once. Fix only
confirmed bucket-1 regressions; file 2 and 3 as known-not-a-bug.

### Ruling out content (the GLB-parse check)

Bucket 2 is settled without a client: pull the deployed entity and parse the GLB
material table straight from its JSON chunk (no Unity needed). A **material-less
primitive** (`primitives[].material == null`, `materials: []`) is the tell that
the scene colours/textures it at *runtime* via the SDK — so a flat colour there
is content, not a missing reference.

```python
import struct, json, urllib.request
# h = a content hash from POST /content/entities/active {"pointers":["x,y"]}
d = urllib.request.urlopen(f"http://localhost:5141/content/contents/{h}").read()
assert d[:4] == b'glTF'
clen = struct.unpack('<I', d[12:16])[0]
j = json.loads(d[20:20+clen])
# j["materials"][i]["pbrMetallicRoughness"]["baseColorFactor" | "baseColorTexture"]
```

### Render equivalence, not pixel equality

When comparing renders (e.g. asset-bundle parity, or before/after a change),
judge by an **alpha-weighted perceptual mean**, never the worst single texel — a
255-off texel under a transparent pixel is invisible. The render-equivalence
taxonomy (defined in `abgen-rs/docs/methodology/render_equivalence_taxonomy.md`)
grades a divergence into one of eight tiers:

- **G1** byte-identical · **G2** decode-identical (bytes differ, pixels same) ·
  **G3** imperceptible (<~½ level avg) · **G3b** marginal (~½–2 levels) ·
  **G4** non-texture value noise (mesh/anim, no texture) · **G5** sampler-state
  divergence (format/colour-space/wrap/filter/mip) · **G6** visible (>~2 levels
  avg or real alpha divergence) · **G7** structural/binding (renders broken) ·
  **G8** undecodable.

G1–G3 are render-equivalent; **G5–G8 are the short list to investigate.** Always
compare against a **control** in the same session (a base binary's own
base-vs-base diff is the floor), not an absolute.

### Triage: defect vs quirk vs wall

When a divergence (byte or pixel) is real, sort it into exactly one of three —
and only the first is worth chasing:

1. **Fixable defect** — our side is wrong. Derive the rule from the reference
   bytes + source, fix it, and gate **zero-regression** (every previously
   matching output must still match).
2. **Engine quirk** — the engine does something unusual and *matching it* is
   correct (e.g. dual-bound color+normal textures import swizzled; EXIF
   orientation ignored). Source-referee confirms our output matches the engine,
   not the source.
3. **Irreducible wall** — confirmed not derivable in a clean room (BC7 encoder
   float-order, mesh `RecalculateTangents` f32 op-order, emote
   AnimatorController `m_TOS` iteration order). Documented in
   `abgen-rs/docs/walls/`; don't re-drill.

### Visual regression

Capture deterministic frames (freeze time-of-day, settle past dithering) from
the base build twice → that's the noise floor. A frame from the change only
indicates a real difference when it exceeds that base-vs-base floor. SSIM / PSNR
+ heatmaps localize it.

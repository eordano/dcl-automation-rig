# Cross-cutting — visual-fidelity tour (driving the bevy battery)

> **Status:** Not verified. Drives an external (bevy-explorer) tour; not run from this rig.

A "fidelity tour" teleports a guest avatar through a list of real Genesis City
scenes/games on a live realm and screenshots each one **fully loaded**, as an
average user would see it (normal follow-cam + UI, noon lighting). It's how you
get an at-a-glance gallery proving scenes actually render — and a base for visual
regression across branches.

The working implementation lives in **bevy-explorer** (`benchmark/fidelity/`,
plus a `--benchmark_tour` mode in the binary). This rig **drives it as an
external** — it does not modify that repo. [`bevy/fidelity-tour.sh`](../bevy/fidelity-tour.sh)
is a thin launcher over the tour runner that already exists there.

## Run it

```bash
# point at your bevy checkout (default ~/dcl/bevy-explorer); pick an output dir
DCL_BEVY_REPO=~/dcl/bevy-explorer ./bevy/fidelity-tour.sh ~/dcl/bench/fidelity
```

Prerequisites the launcher checks for you:
- The tour tooling is present in the bevy tree (`benchmark/fidelity/run-tour.sh`,
  `locations.json`, and the `--benchmark_tour` flag). **It may be uncommitted** —
  don't `git reset`/`git clean` it away.
- The binary is built: `target/release/decentra-bevy`. If missing, build it in
  the FHS shell: `dcl-shell -c 'cd <bevy> && cargo build --release'`.
- Run as the user whose `dcl-shell` the runner points at (the bevy runner
  hardcodes an absolute FHS-shell path — check the top of its `run-tour.sh`).

Output: `tour_<idx>_<x>_<y>.png` per stop + `tour.log`, in the output dir. View
the gallery, read the PNGs.

## What the tour does (and the knobs that matter)

The bevy runner owns GPU sway, permission auto-grant, and capture. The pieces
worth understanding because they're the difference between real shots and bare
terrain:

- **Settle heuristic** (the crux). After each teleport it waits for a real
  ready state before shooting: **`still_loading==0 && pending GLTFs==0 && every
  scene ticking>10`**, held for **90 frames** — but only *after* a **240-frame
  floor** post-teleport. The floor exists because right after a jump the new
  parcel hasn't enqueued its GLTF loads yet, so a transient `pending==0` would
  fire an empty green-screen shot. Hard cap **1800 frames (~30s)** per stop so a
  scene that never settles doesn't hang the tour. (This fix materially raised the
  share of stops captured fully-loaded; the rest are honest open-spawn cases.)
- **Determinism for fidelity**: time-of-day frozen at **noon** (live TOD is
  often dark), a **fixed guest seed** (same avatar/wearables every run), and the
  player placed **inside** the target parcel (the scene-bounds shader dims
  geometry outside the player's scene).
- **`DCL_AUTO_ALLOW=1`**: auto-grants scene permission dialogs so modals don't
  block the shots. Headless/benchmark only.
- **GPU headless sway** (`WLR_RENDERER=gles2`, GPU-rendered high-refresh output),
  *not* the rig's pixman software sway — pixman holds swapchain buffers for tens
  of milliseconds and paces clients to a low frame rate. See
  [`03-linux-alternatives.md`](03-linux-alternatives.md). Gotcha: the
  GPU sway must close the bench flock fd (`9>&-`) or an orphaned sway keeps the
  lock open and deadlocks the next run.

## Refresh the scene list

The stops in `benchmark/fidelity/locations.json` come from the most-active list:

```bash
curl 'https://places.decentraland.org/api/places?order_by=most_active&limit=40'
```

Edit that file in the bevy repo to change stops (that's editing the *battery
data*, which the maintainer owns — outside this rig's "don't modify bevy" rule;
do it there if you own the list).

## Applying the same idea to the Unity player

The technique is renderer-agnostic; only the driver differs. To get an
equivalent Unity gallery without a bevy-style tour mode:

- **Built player**: loop over the scene list, launch the player per stop with
  `--realm <url> --position x,y` (+ the deterministic flags from
  [`00-shared.md`](00-shared.md): `--skybox-time-enabled false`,
  `--disable-hud`, `--resolution …`, `--skip-minimum-specs-screen --debug`),
  let it reach `LoadingStatus.Completed`, then screenshot the rig display
  (`dcl_headless_shot`) before the next stop.
- **Editor**: drive [`ClaudeIPC`](01-linux-editor.md) — `world-ready`, then an
  `exec` of a teleport method, then `dcl_headless_shot` per stop.

Either way, port the **settle discipline**: a floor after the teleport, a real
readiness check (not just "in world"), and a per-stop timeout — otherwise you
capture loading screens.

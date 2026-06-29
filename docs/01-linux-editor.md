# Target 1 — Linux editor (headless)

> **Status:** Partly verified. The headless display, editor **boot + license + full
> compile + layout load** are verified (Unity 6000.4.0f1, Unity Pro license accepted).
> But the editor then **can't present its window** on this host's software Xwayland
> (`No DRI3` under Vulkan; no GLX under `-force-glcore`) with no presenting GPU
> available — so it exits at layout, and ClaudeIPC driving / the warm flow /
> license-seeding are **NOT** verified (they'd run only in a presented editor).

Run the Unity Editor on a Linux box with no monitor, screenshot it, and drive
it programmatically. Builds on the headless display
([`lib/headless-display.sh`](../lib/headless-display.sh)) and the file-IPC
([`unity/ClaudeIPC.cs`](../unity/ClaudeIPC.cs)).

## Launch

```bash
export DCL_EXPLORER_REPO=~/unity-explorer
./linux/editor-up.sh
```

That script: brings up the nested sway+wayvnc display → **seeds the Unity
license** → clears stale crash/lock state → runs `disable-test-asmdefs.sh` →
launches `Unity -projectPath … -force-vulkan` into the nested Xwayland → watches
the first 75 s and **retries on the boot NRE**.

### Why each step exists

- **Nested display.** `WLR_BACKENDS=headless` makes wlroots render to memory;
  wayvnc exports it. `xwayland force` because Unity 6's wayland path isn't
  reliable — it wants a real X11 display.
- **bwrap `/tmp/.X11-unix` bind.** The host's `/tmp/.X11-unix` is owned by
  `nobody:nogroup`, which makes wlroots refuse to start Xwayland. We bind a
  user-owned dir over it *inside the namespace only*.
- **`disable-test-asmdefs.sh`.** Unity 6000.4 has a ~50%-of-cold-boots NRE in
  `LifecycleController` triggered by `UNITY_INCLUDE_TESTS` asmdefs in
  PackageCache. Renaming those dirs to `~` (Unity ignores `~` dirs) fixes it.
- **Boot retry.** Even with the asmdef fix, the occasional boot dies; the
  warmup watcher kills and relaunches (`DCL_EDITOR_RETRIES`, default 4).
- **Audio at boot.** FMOD panics if there's no sound server when it starts, so
  `dcl_audio_up` (PipeWire+pulse) runs as part of "up", not on demand.

## Licensing a headless editor (the part that's easy to miss)

A fresh/headless box has no Unity license, and there's no GUI to activate one.
The rig **seeds** the license + Hub state from a snapshot (`$DCL_UNITY_PERSIST`,
default `~/.dcl-rig/unity-config`), copied in **seed-only** (`cp -an` — never
overwrites a live license, so an activated desktop is untouched).

Create the snapshot once, on a box where you've activated interactively:

```bash
P=~/.dcl-rig/unity-config
mkdir -p $P/config-unity3d $P/share-unity3d $P/config-unityhub
cp -a ~/.config/unity3d/.     $P/config-unity3d/
cp -a ~/.local/share/unity3d/. $P/share-unity3d/
cp -a ~/.config/UnityHub/.     $P/config-unityhub/
```

Caveat: after a machine-binding change (e.g. relocating `$HOME`) the seeded
license won't validate — Unity needs one online Hub re-activation, then
re-snapshot.

## Modals & stale state (what wedges a headless boot)

Two extra hazards beyond the boot NRE, both handled:

- **Stale crash state** → a modal you can't click. Before launch the rig removes
  `Temp/UnityLockfile`, `Temp/__Backupscenes`, `Library/CurrentScene`, and any
  leftover `UnityBugReporter` — otherwise you get a "Recover backup scene?"
  dialog blocking boot.
- **Modals that still appear** can be dismissed by OCR-driven clicking (the full
  rig screenshots the editor, OCRs it, and clicks `No` on the scene-recovery
  dialog / `Ignore` on the compile-errors dialog). The consolidated launcher
  prefers to *prevent* them (cleanup above); add OCR dismissal if your project
  still surfaces one.
- **`unity-patches`** — an alternative to `disable-test-asmdefs.sh`: a Mono.Cecil
  binary patch of `OrderedAssemblyList` that fixes the NRE at the assembly level
  (more robust across package refreshes, but heavier to set up). Use the asmdef
  rename by default; reach for the patch if the rename stops covering a refresh.

## Post-boot "warm" (cache identity + enter Play)

After the IPC heartbeat is live, a one-shot **warm** step gets the editor into a
useful state for driving: compile-check → cache a throwaway identity in
PlayerPrefs (so login is instant and offline) → enter Play. Idempotent — it
skips anything already done. Drive it over ClaudeIPC: `compile`, then an `exec`
of the identity-cache method, then trigger Play. Pair with `world-ready`
(below) before issuing world commands.

## Multiple editors / identity isolation

Two editors on one box must not share identity or fight over the IPC dir:

- **Identity**: give the second editor its own `XDG_CONFIG_HOME` /
  `XDG_DATA_HOME` (PlayerPrefs identity lives there) and/or a distinct dev
  wallet via `DCL_DEV_WALLET_JSON` — export them before `editor-up.sh`.
- **IPC**: `ClaudeIPC` stamps each command with the project path and uses a
  per-project heartbeat, so each editor only claims its own commands. (Caveat:
  the watch dir `/tmp/dcl-editor` is a single host singleton — fine for two
  distinct projects, but only one editor *per project* is cleanly driveable.)

## Driving the running editor (ClaudeIPC)

`ClaudeIPC.cs` runs a watcher inside the editor: drop a JSON command file in
`/tmp/dcl-editor/cmd/`, read the reply from `/tmp/dcl-editor/out/`. Operations
include `ping`, `exec` (call any static method by reflection),
`compile`, `scene-roots`, `ui-click`, `ui-list`, `world-ready`, `log`.

```bash
# wait until the world is actually booted (not just "in Play")
echo '{"op":"world-ready"}' > /tmp/dcl-editor/cmd/r1.json
# run an arbitrary static method
echo '{"op":"exec","method":"RenderDiag.ScanErrorShaders"}' > /tmp/dcl-editor/cmd/r2.json
cat /tmp/dcl-editor/out/r2.json
```

Gotchas baked into the C#:

- **`world-ready` ≠ "in Play".** For tens of seconds after entering Play the
  world container doesn't exist and `exec` no-ops; poll `world-ready` first.
- **Per-project heartbeat.** All editors share `/tmp/dcl-editor`; commands are
  stamped with the project path and each editor only claims its own, so two
  editors don't fight over one command. (Caveat: the watch dir is a single host
  singleton — only one editor per box is cleanly driveable.)
- **`/tmp/dcl-editor/cmd` is `chmod 0700`** — writing there is full code
  execution, so it's locked to the owning user.

## Screenshots

```bash
. config.sh; . lib/common.sh; . lib/headless-display.sh
dcl_headless_shot /tmp/editor.png      # grim against the rig's wayland-1
```

## Tuning knobs

| Var | Default | Purpose |
|-----|---------|---------|
| `DCL_RIG_PORT` | 5913 | wayvnc port = rig id (run several in parallel) |
| `DCL_EDITOR_EXTRA_ARGS` | — | extra editor CLI args |
| `DCL_EDITOR_RETRIES` | 4 | boot attempts before giving up |
| `DCL_WLR_RENDERER` | pixman | `pixman` (CPU) or a GPU renderer — see [03](03-linux-alternatives.md) |

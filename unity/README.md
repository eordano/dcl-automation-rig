# unity/ — editor-side C# (drop into the project)

Copy these into a **dedicated sub-folder** of `Assets/Editor/` — NOT the
`Assets/Editor/` root. The `.asmdef` claims every `.cs` in its folder tree, so
dropping it at the root would pull the project's *own* editor scripts (e.g.
`CloudBuild.cs`) into this reference-less assembly and break their compile. A
sub-folder scopes the asmdef to just these files:

```bash
DEST="$DCL_EXPLORER_REPO/Explorer/Assets/Editor/DclHarness"
mkdir -p "$DEST" && cp unity/*.cs unity/*.asmdef "$DEST/"
```

(`mac/` does this for you — see [docs/07-mac.md](../docs/07-mac.md). On a Mac the
`#!/usr/bin/env bash` scripts also resolve the editor at the non-Hub
`/Applications/Unity/Unity-<ver>` layout.)

| File | Role | Used by |
|------|------|---------|
| `BuildScript.cs` | One batchmode build `-executeMethod` per platform (Win/Linux/Mac × release/dev), env-driven (`DCL_BUILD_OUT`, `DCL_BUILD_VERSION`, `DCL_GFX_API`). | every build target |
| `ClaudeIPC.cs` | File-based IPC server inside the editor — drop JSON in `/tmp/dcl-editor/cmd/`, read `/tmp/dcl-editor/out/`. `exec` runs any static method by reflection. | [Linux editor](../docs/01-linux-editor.md) |
| `DclPlaytestHarness.cs` | Reflection-based in-editor harness — **the full current master**. Session: `RunHeadless`. Telemetry: `RunPerfHeadless`, `RunCpuBreakdownHeadless`, `RunShadowPerfHeadless`, `RunRenderDecompHeadless` ([docs/16](../docs/16-telemetry-modes.md)). UI capture: `RunAtlasHeadless` + `RunAuthCaptureHeadless` and the spliced `AtlasCapture_<route>` driver methods ([docs/12](../docs/12-ui-capture-atlas.md)). | [Windows editor](../docs/04-windows-editor.md), perf, atlas |
| `DCL.Harness.Editor.asmdef` | Assembly definition for the harness (editor-only). | — |

`ClaudeIPC.cs` and `DclPlaytestHarness.cs` are faithful copies of the
battle-tested originals — their inline comments document the Unity-6 gotchas
(`[InitializeOnLoadMethod]` vs `[InitializeOnLoad]`, domain-reload re-registration,
exact `ProfilerRecorder` counter names, no `-batchmode` for Play mode). Preserve
those comments; they encode bugs that already bit.

`DclPlaytestHarness.cs` is the **full current master** (~9.4k lines), so it
carries the spliced `AtlasCapture_<route>` UI-capture methods inline. The atlas
*driver sources* (`atlas-drivers-*/`) and *runner scripts* still live in the VM
harness and are referenced, not copied (see [docs/12](../docs/12-ui-capture-atlas.md)) —
this vendored file is the merged result they splice into.

## Mac-parity edits (keep in sync with the VM master)

These additive, env-gated changes let the harness run on Mac via ClaudeIPC
(Windows behavior unchanged). Marked `[Mac-parity addition]` in the source:

- **`REPORT_PATH` / `SHOTS_DIR`** — were `const` Windows paths; now `static
  readonly`, env-overridable via `DCL_HARNESS_REPORT` / `DCL_HARNESS_SHOTS` (the
  editor must be *launched* with them exported). Default to the Windows paths.
- **`TeleportTo(int x, int y)`** — public static teleport entry for IPC-driven
  fidelity tours (finds `RealmNavigator`, reuses `TryTeleport`). `mac/fidelity-tour.sh`.
- **`HideDebugPanel()`** (no-arg) — exec-able overload that hides the dev DEBUG
  PANEL so captures are clean by default.
- **`HideRewardsPopup()`** — deactivates the `NewNotificationPanel` toast/rewards
  popup so it doesn't clutter captures.
- **Deprecation cleanup** — `FindObjectsByType(…, FindObjectsSortMode)` and
  `SetScriptingBackend(BuildTargetGroup, …)` swapped for the non-obsolete Unity-6
  overloads (kills ~17 `CS0618` warnings). Behavior identical.

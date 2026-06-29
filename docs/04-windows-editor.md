# Target 4 — Windows editor

> **Status:** Not verified here. Adapted from the working Windows VM harness; not re-run on a clean host.

Run the editor on a Windows host (bare metal or the VM in
[`06`](06-windows-vm.md)) executing an automation harness, with clean-slate
resets and crash recovery.

## Launch

```powershell
.\windows\reset-and-launch-editor.ps1 `
  -Project 'C:\Users\dcl\unity-explorer\Explorer' `
  -Method  'DCL.Harness.DclPlaytestHarness.RunHeadless'
```

## The two tricks that make it reliable

1. **Cache nuke before boot.** Stale `Library\ScriptAssemblies`, `Library\Bee`,
   `Library\StateCache`, and `Temp` cause domain-reload hangs and phantom
   compile errors on a fresh start. The script kills all Unity processes
   (8 s grace) and deletes those first.

2. **Interactive Scheduled Task, not a direct launch.** A command arriving over
   SSH/WinRM runs in **session 0**, which has *no graphics device* — Unity Play
   mode and rendering silently misbehave there. The script registers a Scheduled
   Task with `LogonType=Interactive` so the editor runs in the real desktop
   session (1). It also starts **Unity Hub first** and waits 15 s so the
   licensing client is warm before the editor asks for a license (a cold
   licensing client is the #1 boot stall, freezing the log at ~103 KB).

## The harness ([`unity/DclPlaytestHarness.cs`](../unity/DclPlaytestHarness.cs))

A reflection-based in-editor harness — it touches ~305 game assemblies *only*
through reflection, so it has no hard compile dependency on them and survives
their churn. Entry points (all static, all `-executeMethod`-able):

| Method | Output | Measures |
|--------|--------|----------|
| `RunHeadless` | `harness-report.json` | session playtest: load Genesis Plaza, time-to-interactive |
| `RunPerfHeadless` | `harness-perf.csv` | paired A/B microbenchmark (feed to `analysis/perf-analyze.py`) |
| `RunCpuBreakdownHeadless` | `harness-cpu.csv` | per-ECS-system CPU time |
| `RunShadowPerfHeadless` | `harness-shadow.csv` | shadow rendering cost |
| `RunRenderDecompHeadless` | `harness-render.csv` | render-pipeline decomposition |
| `RunAuthCaptureHeadless` | `harness-run.log` | drive the auth/login flow headless and cache the identity |

The auth-capture variant has its own reset-launch script,
`reset-and-launch-auth.ps1` (identical reset, `-executeMethod
DCL.Harness.DclPlaytestHarness.RunAuthCaptureHeadless`) — the Windows mirror of
the Linux editor's "warm → sign the auth challenge → cache identity" step
([`01-linux-editor.md`](01-linux-editor.md)). Run it once on a fresh guest so
later sessions log in instantly.

What the C# gets right (and you must preserve):

- **Domain-reload safety.** Play mode triggers a domain reload that wipes
  statics; the harness re-registers its callbacks via `SessionState` +
  `EditorApplication.playModeStateChanged` after every reload.
- **No `-batchmode` for Play mode.** Play mode + `ProfilerRecorder` + rendering
  are unreliable headless — run it windowed via the Scheduled Task (which is
  exactly why we use the task instead of `-batchmode`).
- **Exact `ProfilerRecorder` counter names.** They're verified against engine
  source; a typo fails silently (returns no samples), so don't "tidy" them.

## Polling from outside

The run writes its log and a JSON report to known paths; poll their sizes to
detect progress, completion (`json` appears), or a stall (log size frozen below
the play threshold). The VM target's [`run-playtest.sh`](../vm/run-playtest.sh)
implements the full self-healing loop on top of this — the same logic applies on
bare-metal Windows, just without the SSH hop.

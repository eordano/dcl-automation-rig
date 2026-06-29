# Cross-cutting — in-editor telemetry & benchmark modes

> **Status:** Not verified on this host. The harness modes are a real, current
> work product in the VM harness, but they measure a *running, rendering* editor
> in Play mode — which can't present on this host (no GPU — see
> [03](03-linux-alternatives.md)). The scripts here are the generalized,
> rig-conventional form of the working VM-harness originals; the **method** is
> sound (and the analyzers in [`analysis/`](../analysis/) are verified on
> synthetic data), but a live run was not executed here. Treat as design intent
> until run against a GPU-backed editor.

[`docs/08`](08-testing.md) lays out the *discipline* (only interleaved A/B inside
one session is trustworthy; collapse to medians; report the whole shape). This
doc is the *production* side — the four `-executeMethod` entry points in
[`../unity/DclPlaytestHarness.cs`](../unity/DclPlaytestHarness.cs) that generate
the data that discipline analyzes. They complement the perf-analysis scripts: the
harness modes **capture**, `analysis/` **judges**.

## The four modes

| Mode | Entry point | Writes | Consumed by | What it measures |
|---|---|---|---|---|
| `perf` | `RunPerfHeadless` | `harness-perf.csv` | [`analysis/perf-analyze.py`](../analysis/perf-analyze.py) | Paired within-session A/B on one render pipeline (the only trustworthy kind). |
| `cpu` | `RunCpuBreakdownHeadless` | `harness-cpu.csv` | printed (ranked) | Every CPU time-marker at steady idle, ranked by per-frame ms — a "where does the frame go" decomposition. |
| `render` | `RunRenderDecompHeadless` | `harness-render.csv` | [`analysis/perf-analyze-multi.py`](../analysis/perf-analyze-multi.py) | Multi-knob render decomposition — toggles shadows / MSAA / SRP-batcher / … and attributes cost to each. |
| `shadow` | `RunShadowPerfHeadless` | `harness-shadow.csv` | [`analysis/perf-analyze.py`](../analysis/perf-analyze.py) | Shadows-on vs shadows-off A/B (a focused single-knob case of `render`). |

All four are reflection-only (no compile-time dep on DCL types), arm Play mode,
run their measurement loop, write one CSV, and exit — the same shape as the
session harness (`RunHeadless`), just emitting a CSV instead of the session JSON.

## Running one

```bash
vm/run-telemetry.sh <perf|cpu|render|shadow> [max_attempts]   # default 3
```

`vm/run-telemetry.sh` is the host-side driver. It reuses
[`vm/run-playtest.sh`](../vm/run-playtest.sh)'s self-healing babysitter — reset +
launch in the guest, poll the run log, and retry from scratch through the two
known pre-play stalls (licensing freeze, compile freeze) — but waits for the
mode's CSV instead of the session JSON. On success it scp's the CSV to
`vm/reports/telemetry/<mode>-<utc>.csv` and runs the matching analyzer (or, for
`cpu`, prints the ranked table).

Guest side (one parameterized launcher + poller instead of four copies each):

- [`windows/reset-and-launch-telemetry.ps1`](../windows/reset-and-launch-telemetry.ps1)
  — same cache-nuke + Interactive-logon Scheduled Task trick as
  [`reset-and-launch-editor.ps1`](../windows/reset-and-launch-editor.ps1)
  (Play mode needs a real desktop session — session 0 has no graphics device),
  parameterized by `$Mode`.
- [`windows/poll-telemetry.ps1`](../windows/poll-telemetry.ps1) — one status line
  (`unity=… log=… csv=…`) the babysitter reads.

Because a piped-over-SSH script can't take `-params`, the host driver prepends
`$Mode='…'` before piping; the ps1 reads `$Mode` with a `'perf'` fallback.

## Why these aren't `analysis/`'s job

`analysis/perf-analyze*.py` are pure, host-agnostic stats over a CSV — they were
the easy half to vendor and verify. The hard half is *producing a trustworthy
CSV*: arming Play mode in a real desktop session, interleaving the A/B conditions
inside one session under a lock, sampling the right `ProfilerRecorder` counters.
That half is platform-specific and lives here.

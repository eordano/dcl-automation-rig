# Cross-cutting — built-player perf (AutoPilot, the perf oracle)

> **Status:** Mixed. The AutoPilot mode itself is **confirmed in upstream
> source** — `--autopilot --csv <f> --summary <f>` (AppArgsFlags), a ~90 s
> CPU+GPU sample via `ProfilerRecorder` (PerfSampler), the version-gate bypass
> (`MainSceneLoader.DoesApplicationRequireVersionUpdateAsync`, also skipped by
> `--skip-version-check`), and `Application.Quit` on completion. The
> fixed-crowd injector, the per-ECS-group split log lines, and the dev-build
> entry/wallet conventions below are **local/fork tooling** (not in the on-disk
> mirror) — documented from the perf campaign and env-overridable here. Not run
> on this host: like the editor and Proton player, the built player gets to the
> GUI step but can't present (no GPU — see [03](03-linux-alternatives.md)).

[`08`](08-testing.md) says it in one line — *the built player is the perf oracle,
not the editor.* This doc is the how. The editor is single-threaded and
session-age-confounded (its frame time drifts with how long it's been open and
never has the real main/render-thread split), so editor numbers can't carry a
headline A/B. A built **Development** player has the real threads and real GPU
counters; AutoPilot drives it deterministically. ([`16`](16-telemetry-modes.md)
is still useful — but for *decomposition*, "where does the frame go," not the
headline delta.)

## 1. Build a Development player

The profiler/GPU counters need a **Development** build (`BuildOptions.Development`),
so use the dev entry point — [`unity/BuildScript.cs`](../unity/BuildScript.cs)'s
`BuildLinux64Dev` (the rig's consolidation of the upstream player build; see
[`00`](00-shared.md) for the exact `-executeMethod` CLI). The editor must be
**down** first — a batchmode build opens its own.

```bash
# (from the editor / batchmode) BuildScript.BuildLinux64Dev → build/Linux/decentraland-explorer.x86_64
```

**Build-staleness trap (cost ~3 cycles on the campaign):** if you edit a `.cs`
*after* a build finishes, the player runs the **stale binary**. Verify before
trusting a run:

- the source mtime must be **older** than the built assembly's DLL mtime;
- to confirm a specific change shipped, grep the DLL — but **.NET string
  *literals* live in the UTF-16LE `#US` heap**, so a UTF-8 byte search finds
  member *names* and misses literals. Search both encodings:

```python
d = open("build/Linux/decentraland-explorer_Data/Managed/<Assembly>.dll","rb").read()
print("utf8 member :", b"myNewSymbol" in d)
print("utf16 literal:", "my new log line".encode("utf-16-le") in d)
```

- **which assembly a `.cs` compiles into is not obvious from its folder** — a
  file under `AvatarShape/` can land in a different `.asmdef` than you'd guess.
  Read the nearest enclosing `.asmdef` to know which DLL to grep; don't assume.

## 2. Run an AutoPilot perf capture

AutoPilot auto-logs-in with the throwaway dev wallet, waits
`LoadingStatus.Completed`, samples CPU+GPU frame time for ~90 s, then quits:

```bash
. config.sh
./linux/binary-native.sh -- --autopilot --csv /tmp/perf.csv --summary /tmp/perf.txt \
    --realm "$DCL_REALM" --position 52,-60 --skip-version-check
```

`--skip-version-check` is implied by `--autopilot`, but pass it explicitly when a
global min-version gate would otherwise block the boot. The two NixOS ABI shims
the binary needs (`v8-deepbind.so` `LD_PRELOAD`, `GLIBC_TUNABLES` AVX-512
disable) are wired into [`linux/binary-native.sh`](../linux/binary-native.sh) and
documented in [`02`](02-linux-binary.md) — without the AVX-512 disable the Boehm
GC over-reads on the EVEX `memcpy` variants and SIGSEGVs during scene load (so
`frames=0` / never-`Completed` is usually that flaky crash — just re-run).

The summary reports a CPU/GPU percentile table (avg, 1% / 0.1% worst, max) with a
CPU/GPU-bound verdict, plus (fork instrumentation) the main-thread split by ECS
root group (`[AUTOPILOT-GROUP]` log lines) and the top Unity-native time markers
(auto-discovered via `ProfilerRecorder` / `ProfilerRecorderHandle.GetAvailable`,
filtered to `TimeNanoseconds` — these nest, carrying the PlayerLoop hierarchy).
The per-frame CSV is what [`analysis/perf-analyze.py`](../analysis/perf-analyze.py)
judges.

## 3. Fixed-population crowd benchmark

For crowd-scaling changes (animation culling, skinning throttle) the live-plaza
avatar count varies run-to-run and swamps the signal. A **fixed** synthetic crowd
makes it deterministic — in the campaign fork, `DCL_BENCH_AVATARS=N` (a
dev-only path) spawns N idle avatars at spawn. Confirm via the breadcrumbs (logged
at *error* level on purpose — the `AVATAR` category is errors-only in player
builds):

```
[BENCH] requesting 50 benchmark avatars
[BENCH] spawned 50 avatars (randomizers=2)
```

`randomizers=2` = base male/female bodies (the base-wearable collection promise
fails on a local stack, so the bench falls back to base bodies and still spawns
deterministically). This injector is **not in the upstream mirror** — it's the
Unity-side analog of bevy's `--benchmark_fake_players` ([13](13-bevy-benchmark.md));
if your fork lacks it, avatar count is uncontrolled and crowd numbers are noise.

## 4. The discipline (same as everywhere)

Interleaved A/B inside one session only; collapse each window to its median;
report the whole shape; a delta counts only past the noise floor you establish by
A/B-ing the *same* commit. For a crowd change: characterize the baseline band
over several fixed-population runs, apply the change, rebuild (§1 — verify it's
not stale), run the **same** count again, compare the tightest markers
(`Animation/Animators.Update`, `…PresentationSystemGroup` are stabler than the
`ProcessRootMotion`/`ProcessAnimation` ones). **Commit only if** the relevant
markers drop beyond the band **and** a screenshot shows the avatars still render
correctly — otherwise revert and report honestly. The whole point is to stop
shipping perf changes blind. Full discipline: [`08`](08-testing.md).

## Relation to the rest of the rig

The **built-player** perf path. [`16`](16-telemetry-modes.md) is the in-editor
decomposition counterpart (use it to see *where* the frame goes, not for the
headline); [`13`](13-bevy-benchmark.md) / [`17`](17-wasm-web-measurement.md) are
the bevy native / web counterparts; all share the discipline in
[`08`](08-testing.md) and the paired stats in
[`analysis/perf-analyze.py`](../analysis/perf-analyze.py).

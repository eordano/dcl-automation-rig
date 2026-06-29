# rig/analysis — renderer-agnostic paired A/B statistics

The web-stack port of the rig's **perf-analysis-stats** capability. Both scripts
are **PORTED VERBATIM** from the Unity rig — they are pure stdlib and *renderer-
agnostic by construction*: they consume a paired-within-session A/B CSV and say
whether a delta is real. Nothing in the statistics knows or cares whether the
CSV came from a Unity harness, a wasm orbit, a native frame-time stream, or a
backend latency probe. That is the whole point of keeping them unchanged.

> The docstrings still mention "DclPlaytestHarness perf mode" / "preview camera"
> as the *example* A/B that produced the original CSV format. That framing is
> historical — the CSV schema (`drop,window,cond,cpu_ms,gpu_ms`) is what matters,
> and every web-stack producer below emits it.

## Files

| File | Verdict | What |
|---|---|---|
| `perf-analyze.py` | **PORTED verbatim** | paired within-session A/B over one CSV: window medians → Mann-Whitney U (normal approx, tie-corrected) → bootstrap 95% CI on `median(A) − median(B)`. **A delta is real only when the CI excludes 0.** |
| `perf-analyze-multi.py` | **PORTED verbatim** | multi-knob render-decomposition: same stats per `knob` column, ranked by CPU cost. |

## What feeds it in the web stack (three CSV producers, one consumer)

The same significance discipline governs engine perf and backend perf alike —
this is the shared consumer at the bottom of three pipes:

1. **Web — `rig/bevy/web-bench-ab.sh`** → `orbit-ab.csv`.
   - With a `DCL_WASM_BENCHMARK` build: `cpu_ms` = `orbit_cpu.p50` per run
     (low-variance, the real metric).
   - **Without it (the case in THIS checkout):** the orbit path is GATED; the
     `submitFps` fallback (wrap `GPUQueue.submit`) is what's available, so web
     metrics today are **submitFps-only** until the WASMBENCH build lands. The
     worker-gap and the missing benchmark build are why — see
     [`../docs/bridge-status.md`](../docs/bridge-status.md) and
     [`../bevy/BUILD-WASM-BENCHMARK.md`](../bevy/BUILD-WASM-BENCHMARK.md).
2. **Native — `rig/bevy/bench-ab.sh`** → per-frame frame-time CSV (interleaved
   A,B native `decentra-bevy` binaries). `cpu_ms`/`gpu_ms` straight from the
   bevy harness frame stream.
3. **Backend — a catalyst-telemetry latency A/B** → see
   [`catalyst-telemetry-ab.md`](catalyst-telemetry-ab.md): pull a paired
   p90-request-latency CSV (e.g. catalyst vs the reference org backend) and run
   the SAME `perf-analyze.py`.

```bash
./perf-analyze.py ~/dcl/bench/web-ab/orbit-ab.csv        # web A/B (cond A vs B)
./perf-analyze.py /tmp/catalyst-latency-ab.csv          # backend A/B (same tool)
./perf-analyze-multi.py /tmp/render-knobs.csv            # per-knob decomposition
```

## The CSV contract (all three producers honor it)

```
drop,window,cond,cpu_ms,gpu_ms
0,1,A,3.91,2.10
0,2,B,4.07,2.11
...
```

- `drop=1` rows (transition/warmup samples) are excluded.
- `window` groups samples collapsed to a median (per-frame samples are
  autocorrelated + heavy-tailed; one robust unit per window).
- `cond` is literal `A`/`B` (the script groups on exactly those).
- A negative `cpu_ms`/`gpu_ms` marks "no sample" and is skipped.

See [`../docs/08-testing-discipline.md`](../docs/08-testing-discipline.md) for
the governing methodology (interleaved-only, noise floor, report the whole
shape with headline p90).

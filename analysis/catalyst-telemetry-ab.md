# Pulling a paired A/B CSV out of catalyst-telemetry

`perf-analyze.py` is renderer-agnostic — it only needs a paired-within-session
A/B CSV in the `drop,window,cond,cpu_ms,gpu_ms` schema. This doc shows how to
produce that CSV from a **backend** A/B (catalyst vs a reference org backend, or
two catalyst builds), so the SAME significance discipline that gates engine
frame-time deltas also gates request-latency deltas.

## What catalyst-telemetry is (and isn't)

`catalyst/crates/catalyst-telemetry` is an **event-ingest** service
(sentry/segment-style handlers + an admin/dashboard; see `src/handlers/`,
`src/lib.rs` `IngestControl`). It does NOT expose a pre-aggregated
p90-latency-percentile endpoint we can read directly — so the A/B latency
numbers come from **measuring request round-trips ourselves** against the two
backends, exactly as an interleaved A/B, and reusing telemetry as the *consumer
of record* if you want the runs logged.

> Honest scope: there is no turnkey `GET /p90` here. The CSV below is produced
> by a tiny interleaved probe (curl timing), which is the trustworthy way to get
> a paired latency sample anyway — a single yesterday-vs-today p90 from a
> dashboard is exactly the between-session comparison the methodology forbids.

## The interleaved probe → CSV

Same rule as every other A/B in this rig: **interleave A,B,A,B in one session**
so both backends see the same machine/network state per pair; collapse each run
to a window; compare window medians. Map latency-ms onto the `cpu_ms` column
(the script is unit-blind):

```bash
# A = catalyst, B = the reference org backend (or build A vs build B).
A_BASE="${DCL_CATALYST_BASE:-http://127.0.0.1:8099}"   # catalyst under test
B_BASE="${DCL_REF_BASE:-https://peer.decentraland.org}" # reference backend
PATHQ="${DCL_AB_PATH:-/lambdas/explore/realms}"          # one representative endpoint
CSV=/tmp/catalyst-latency-ab.csv
echo "drop,window,cond,cpu_ms,gpu_ms" > "$CSV"
win=0
for pair in $(seq 1 30); do
  for cond in A B; do
    base="$A_BASE"; [ "$cond" = B ] && base="$B_BASE"
    win=$((win+1))
    # 8 samples per window; total_time in seconds -> ms. -1 gpu_ms = "no sample".
    for i in $(seq 1 8); do
      ms=$(curl -s -o /dev/null -w '%{time_total}' "$base$PATHQ" \
           | awk '{printf "%.3f", $1*1000}')
      echo "0,$win,$cond,$ms,-1" >> "$CSV"
    done
  done
done
./perf-analyze.py "$CSV"     # delta = median(A) - median(B) ms; real only if CI excludes 0
```

`perf-analyze.py` reports `median A`, `median B`, `delta` and the bootstrap 95%
CI; a backend regression is real **only when that CI excludes 0** — identical to
the engine-perf verdict. Report the whole shape (p25..p99, headline p90) per the
methodology in [`../docs/08-testing-discipline.md`](../docs/08-testing-discipline.md).

## If you want the runs logged through telemetry

The telemetry ingest handlers accept sentry/segment-shaped events; you can POST
one event per A/B run (cond + latency-ms as a measurement) so the dashboard
carries the history. That is a *record*, not the *verdict* — the verdict always
comes from `perf-analyze.py` on the paired CSV, never from eyeballing a
dashboard percentile.

#!/usr/bin/env python3
"""Analyze a paired within-session A/B perf CSV (from DclPlaytestHarness perf mode).

Design (see dcl-loop-state.md perf-methodology note):
- A = target subsystem ON (preview camera enabled), B = OFF (skipped).
- Conditions are interleaved in counterbalanced ABBA ~1s windows within ONE session,
  so between-session variance (the dominant confound) cancels.
- Per-frame CPU is autocorrelated + heavy-tailed (GC), so we DON'T treat frames as iid:
  we collapse each window to its MEDIAN (one robust independent-ish unit per window),
  then compare the A-window medians vs B-window medians.
- Significance: Mann-Whitney U (non-parametric, normal approx with tie correction).
- Effect + uncertainty: bootstrap 95% CI on median(A) - median(B).
  delta = median(A) - median(B) = the subsystem's true per-frame cost.

Usage: perf-analyze.py <csv> [--boot 20000] [--seed 1]
Pure stdlib (no numpy/scipy).
"""
import sys, csv, math, random, statistics as st

def median(xs): return st.median(xs) if xs else float("nan")

def mann_whitney_p(a, b):
    """Two-sided Mann-Whitney U p-value, normal approximation with tie correction."""
    na, nb = len(a), len(b)
    if na == 0 or nb == 0: return float("nan")
    combined = sorted([(v, 0) for v in a] + [(v, 1) for v in b])
    # average ranks (1-based), handling ties
    ranks = [0.0] * len(combined)
    i = 0
    n = len(combined)
    tie_term = 0.0
    while i < n:
        j = i
        while j + 1 < n and combined[j + 1][0] == combined[i][0]:
            j += 1
        avg_rank = (i + 1 + j + 1) / 2.0
        for k in range(i, j + 1):
            ranks[k] = avg_rank
        t = j - i + 1
        if t > 1: tie_term += t ** 3 - t
        i = j + 1
    Ra = sum(r for r, (_, g) in zip(ranks, combined) if g == 0)
    Ua = Ra - na * (na + 1) / 2.0
    Ub = na * nb - Ua
    U = min(Ua, Ub)
    mu = na * nb / 2.0
    N = na + nb
    sd = math.sqrt((na * nb / 12.0) * ((N + 1) - tie_term / (N * (N - 1))))
    if sd == 0: return 1.0
    z = (U - mu) / sd
    return 2.0 * (1.0 - 0.5 * (1.0 + math.erf(abs(z) / math.sqrt(2))))

def bootstrap_ci(a, b, R, seed):
    rng = random.Random(seed)
    diffs = []
    na, nb = len(a), len(b)
    for _ in range(R):
        ra = [a[rng.randrange(na)] for _ in range(na)]
        rb = [b[rng.randrange(nb)] for _ in range(nb)]
        diffs.append(median(ra) - median(rb))
    diffs.sort()
    lo = diffs[int(0.025 * R)]
    hi = diffs[int(0.975 * R)]
    return lo, hi

def analyze(metric, win_meds):
    A = [m for (c, m) in win_meds if c == "A" and not math.isnan(m)]
    B = [m for (c, m) in win_meds if c == "B" and not math.isnan(m)]
    mA, mB = median(A), median(B)
    delta = mA - mB
    p = mann_whitney_p(A, B)
    lo, hi = bootstrap_ci(A, B, R, SEED)
    sig = "YES" if (lo > 0 or hi < 0) else "no"
    print(f"\n== {metric} ==")
    print(f"  windows: A={len(A)}  B={len(B)}")
    print(f"  median A (on)  = {mA:.4f} ms")
    print(f"  median B (off) = {mB:.4f} ms")
    print(f"  delta (A-B = subsystem cost) = {delta:.4f} ms   95% CI [{lo:.4f}, {hi:.4f}]")
    print(f"  Mann-Whitney p = {p:.2e}   significant(CI excludes 0)= {sig}")
    return delta, lo, hi, p

if __name__ == "__main__":
    args = sys.argv[1:]
    R = 20000; SEED = 1
    path = None
    i = 0
    while i < len(args):
        if args[i] == "--boot": R = int(args[i+1]); i += 2
        elif args[i] == "--seed": SEED = int(args[i+1]); i += 2
        else: path = args[i]; i += 1
    if not path:
        print("usage: perf-analyze.py <csv> [--boot N] [--seed N]"); sys.exit(2)

    raw = open(path, encoding="utf-8", errors="replace").read().strip()
    if raw.startswith("ERROR"):
        print("harness reported error:", raw); sys.exit(1)

    rows = list(csv.DictReader(raw.splitlines()))
    # group per-window samples (drop transition frames + invalid samples)
    from collections import defaultdict
    cpu_by_win = defaultdict(list); gpu_by_win = defaultdict(list); cond_of = {}
    for r in rows:
        if r["drop"] == "1": continue
        w = int(r["window"]); cond_of[w] = r["cond"]
        cpu = float(r["cpu_ms"]); gpu = float(r["gpu_ms"])
        if cpu >= 0: cpu_by_win[w].append(cpu)
        if gpu >= 0: gpu_by_win[w].append(gpu)

    cpu_meds = [(cond_of[w], median(cpu_by_win[w])) for w in sorted(cpu_by_win)]
    gpu_meds = [(cond_of[w], median(gpu_by_win[w])) for w in sorted(gpu_by_win)]
    n_samples = sum(len(v) for v in cpu_by_win.values())
    print(f"file: {path}")
    print(f"windows: {len(cpu_meds)}   usable frame-samples: {n_samples}   bootstrap R={R}")

    analyze("CPU main-thread (ms/frame)", cpu_meds)
    analyze("GPU frame time (ms/frame)", gpu_meds)
    print("\nInterpretation: delta = median(A on) - median(B off) = cost ATTRIBUTABLE to the toggled")
    print("subsystem for THIS run. CI excluding 0 => statistically real.")

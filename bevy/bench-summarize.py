#!/usr/bin/env python3
"""Summarize an A/B bevy benchmark: frame-time headlines + (if instrumented)
per-scene startup phases.

Reads two results dirs produced by bevy/bench-ab.sh (each holds run_*.json from
the bevy harness, plus run_*.json.log). Prints, side by side:
  - load_secs, loading hitches (>50ms / >100ms frames), loading p50
  - a per-scene startup table IF the binary emits [scene-startup] log lines
    (an optional instrumentation; the table is simply skipped when absent).

Usage: bench-summarize.py <A_dir> <B_dir> [A_label B_label]
"""
import glob
import pathlib
import re
import statistics
import sys

# Optional per-scene startup instrumentation: a binary built with the
# scene-startup probe prints one such line per scene. Absent in stock builds.
SCENE_RE = re.compile(
    r'\[scene-startup\] (?:snapshot=(\S+) )?title="([^"]+)" hash=(\S+) '
    r'js_kb=(\d+) runtime_ms=([\d.]+) loader_ms=([\d.]+) '
    r'onstart_ms=([\d.]+) total_ms=([\d.]+)'
)


def runs(d):
    import json
    out = []
    for f in sorted(glob.glob(f"{d}/run_*.json")):
        j = json.loads(pathlib.Path(f).read_text())
        logp = pathlib.Path(f + ".log")
        log = logp.read_text() if logp.exists() else ""
        scenes = {}
        for m in SCENE_RE.finditer(log):
            snap, title, h, kb, rt, ld, os_, tot = m.groups()
            scenes[title] = dict(js_kb=int(kb), runtime=float(rt),
                                 loader=float(ld), onstart=float(os_), total=float(tot))
        loading = sorted(j.get("loading_frame_ms", []))
        out.append(dict(
            load_secs=j.get("load_secs"),
            hitch50=sum(1 for x in loading if x > 50),
            hitch100=sum(1 for x in loading if x > 100),
            loading_p50=loading[len(loading) // 2] if loading else None,
            scenes=scenes,
        ))
    return out


def agg(rs, k):
    v = [r[k] for r in rs if r[k] is not None]
    if not v:
        return "—"
    return f"{statistics.median(v):.3f} (mean {statistics.fmean(v):.3f}, {min(v):.3f}-{max(v):.3f})"


def main():
    if len(sys.argv) < 3:
        sys.exit("usage: bench-summarize.py <A_dir> <B_dir> [A_label B_label]")
    a_dir, b_dir = sys.argv[1], sys.argv[2]
    la = sys.argv[3] if len(sys.argv) > 3 else "A"
    lb = sys.argv[4] if len(sys.argv) > 4 else "B"
    A, B = runs(a_dir), runs(b_dir)
    if not A or not B:
        sys.exit(f"no run_*.json found ({la}={len(A)}, {lb}={len(B)}) — did bench-ab.sh complete?")

    print(f"{'metric':22} {la:>38} {lb:>38}")
    for k in ("load_secs", "hitch50", "hitch100", "loading_p50"):
        print(f"{k:22} {agg(A, k):>38} {agg(B, k):>38}")

    # Per-scene startup phases — only when the instrumentation was present.
    def phase(rs, p):
        per = {}
        for r in rs:
            for t, s in r["scenes"].items():
                per.setdefault(t, []).append(s[p])
        return per

    pa_rt, pb_rt = phase(A, "runtime"), phase(B, "runtime")
    if not pa_rt or not pb_rt:
        return
    pa_ld, pb_ld = phase(A, "loader"), phase(B, "loader")
    pa_t, pb_t = phase(A, "total"), phase(B, "total")
    kb = {t: s["js_kb"] for r in A for t, s in r["scenes"].items()}
    print()
    print(f"{'scene':28}{'kb':>6} |{la+' rt':>7}{lb+' rt':>7} "
          f"|{la+' ld':>7}{lb+' ld':>7} |{la+' tot':>8}{lb+' tot':>8}")
    totals = [0.0] * 6
    for t in sorted(pa_rt):
        if t not in pb_rt:
            continue
        row = [statistics.median(pa_rt[t]), statistics.median(pb_rt[t]),
               statistics.median(pa_ld[t]), statistics.median(pb_ld[t]),
               statistics.median(pa_t[t]), statistics.median(pb_t[t])]
        totals = [a + b for a, b in zip(totals, row)]
        print(f"{t[:28]:28}{kb.get(t, 0):>6} |{row[0]:>7.1f}{row[1]:>7.1f} "
              f"|{row[2]:>7.1f}{row[3]:>7.1f} |{row[4]:>8.1f}{row[5]:>8.1f}")
    print(f"{'SUM (medians)':28}{'':>6} |{totals[0]:>7.1f}{totals[1]:>7.1f} "
          f"|{totals[2]:>7.1f}{totals[3]:>7.1f} |{totals[4]:>8.1f}{totals[5]:>8.1f}")


if __name__ == "__main__":
    main()

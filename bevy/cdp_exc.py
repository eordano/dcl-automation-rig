#!/usr/bin/env python3
# =============================================================================
# cdp_exc.py — classify wasm engine failures out of a captured console stream.
#
# The product tour ([product-tour-web.sh]) verifies the wasm bundle FUNCTIONALLY
# (no GPU window presentation here means no pixel check — see product-tour.md +
# docs/bridge-status.md). So "did this scene break?" is answered from the console
# stream, not from a screenshot. This module is the classifier the Unity rig's
# bevy/cdp_exc.py was — ADAPTED to read a cdp-capture.py text stream (stdin or a
# file) and bucket every line into one of the known wasm/wgpu failure classes.
#
# Failure classes (the ones that actually fire on this engine):
#   panic              — a Rust panic reached the console ("panicked at ...")
#   wgpu_validation    — a wgpu validation error (the GPU command was rejected)
#   closure_recursive  — "closure invoked recursively or after being dropped"
#                        (the known video-player rejection, see product-tour.md)
#   unhandled_rejection— an unhandled promise rejection from a scene worker
#   crash_overlay      — the post-load env watchdog crash overlay (benign-ish:
#                        fires AFTER scenes tick — NOT a scene failure on its own)
#
# Pure stdlib. Reads stdin or argv[1]. Prints a per-class count summary and exits
# nonzero if any HARD failure (panic / wgpu_validation) was seen — so a tour run
# can gate on it. closure_recursive + crash_overlay are reported but NON-fatal
# (they are known, post-tick, and don't mean a scene didn't run).
#
# Usage:
#   cdp-capture.py WASMBENCH_RESULT 120 | cdp_exc.py
#   cdp_exc.py /path/to/console.log
# =============================================================================
import re
import sys

# (class, fatal, compiled pattern) — order matters: first match per line wins.
CLASSES = [
    ("panic",              True,  re.compile(r"panicked at|RuntimeError: unreachable|wasm.*panic", re.I)),
    ("wgpu_validation",    True,  re.compile(r"wgpu.*validation|Validation Error|GPUValidationError", re.I)),
    ("closure_recursive",  False, re.compile(r"closure invoked recursively or after being dropped", re.I)),
    ("unhandled_rejection", False, re.compile(r"Uncaught \(in promise\)|unhandled rejection", re.I)),
    ("crash_overlay",      False, re.compile(r"crash overlay|env watchdog|watchdog.*crash", re.I)),
]


def classify(line):
    for name, fatal, pat in CLASSES:
        if pat.search(line):
            return name, fatal
    return None, False


def main():
    if len(sys.argv) > 1:
        with open(sys.argv[1], encoding="utf-8", errors="replace") as f:
            lines = f.readlines()
    else:
        lines = sys.stdin.readlines()

    counts = {name: 0 for name, _, _ in CLASSES}
    examples = {}
    fatal_seen = False
    for raw in lines:
        line = raw.rstrip("\n")
        name, fatal = classify(line)
        if name:
            counts[name] += 1
            examples.setdefault(name, line[:200])
            if fatal:
                fatal_seen = True

    print("== wasm console failure classification ==")
    for name, fatal, _ in CLASSES:
        tag = "FATAL" if fatal else "noted"
        n = counts[name]
        line = f"  {name:20} {n:4}  [{tag}]"
        if n and name in examples:
            line += f"   e.g. {examples[name]}"
        print(line)

    if fatal_seen:
        print("verdict: HARD FAILURE (panic or wgpu-validation) — investigate", file=sys.stderr)
        sys.exit(1)
    print("verdict: no hard failure (closure_recursive / crash_overlay are known, non-fatal)")
    sys.exit(0)


if __name__ == "__main__":
    main()

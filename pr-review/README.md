# rig/pr-review — the PR-review loop, stack+compile+capture rewritten for the web stack

The web-stack port of the rig's **pr-review-loop** capability. The platform-agnostic
ORCHESTRATION is ported intact; the Unity-Windows stack+compile+capture steps are
rewritten for cargo + tsc + wasm + react.

## What PORTS (the orchestration core)

| File | Verdict | What |
|---|---|---|
| `pr-pick.py` | **PORTED** | unauthenticated GitHub PR rotation, head-SHA-change detection, review-files-as-memory. Only the default repo slug changed (`DCL_REVIEW_REPO_SLUG`). |
| `pr-write-review.py` | **PORTED** | PASS / REGRESSION / CONFLICT / COMPILE-FAIL verdict rendering. Same state machine; wording retargeted at the web-stack compile + capture. |
| `pr-codereview.sh` | **PORTED** | headless `claude -p` rubric review on a 3-dot diff — runs anywhere. Retargeted at the web-stack checkout + base branch. |
| `pr-recheck.py` | **PORTED verbatim** | confirm-before-flag re-verify of broken-beyond-baseline screens. |
| `pr-broken.py` | **PORTED** | broken-beyond-baseline routes (comment-skip baseline loader fix). |
| `pr-count.py` | **PORTED verbatim** | open-screen-marker count (0 = incomplete run). |
| `pr-review-baseline.txt` | data | EXPECTED-broken routes (empty for the web stack; GATED ≠ broken). |

Ported principles: empty-cherry-pick `--skip`, confirm-before-flag re-verify,
measured-baseline (capture on a freshly-stacked tree), **never push**.

## What is REWRITTEN (`pr-review-tick.sh`)

| Step | Unity rig | web-stack |
|---|---|---|
| stack | scp `pr-stack.ps1` to a Windows guest; rebase + cherry-pick 44 commits there | `git rebase` onto fresh base + `git cherry-pick $BASE..$FIX` with empty-`--skip`, locally |
| compile | Unity batchmode (`reset-and-batchcompile.ps1`), count `error CS` | `cargo check` bevy-explorer + `tsc`/build sites/app + `cargo check` catalyst, count `error[..]:` / `error TS` |
| capture | VM warm atlas (`launch-atlas-warm.ps1`), scp PNGs | `rig/atlas/url-walk.sh` (HUD routes) + `rig/bevy/product-tour-web.sh` (wasm functional) |

## What is DROPPED

`pr-stack.ps1`, `reset-and-batchcompile.ps1`, `launch-atlas-warm.ps1`,
`poll-*.ps1`, the QEMU/SSH guest, `vmssh.sh` — all Unity-Windows-VM-coupled, no
web-stack analog.

## Honest boundary

Capture is **functional** (WebGPU canvas non-present here) and identity-bearing
auth variants are **GATED** on the worker-gap — the review says so
(`pr-write-review.py` emits the note), so a PR is never blamed for a surface the
engine cannot yet wire on the main thread. See
[`../docs/bridge-status.md`](../docs/bridge-status.md).

## Run

```bash
DCL_REVIEW_REPO_SLUG=decentraland/bevy-explorer \
DCL_REVIEW_CHECKOUT=~/projects/bevy-explorer \
  ./pr-review-tick.sh            # one tick: pick → stack → compile → capture → review (NEVER pushes)
```

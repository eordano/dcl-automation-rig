# Cross-cutting — the PR-review loop (review open PRs against the fix stack)

> **Status:** Verified running. This is the **operational, hardened** set, vendored
> from the VM harness where the loop runs on a schedule end-to-end (pick open PR →
> stack the fix branch → batchmode compile → full warm atlas → re-verify → review),
> producing real PASS / REGRESSION / CONFLICT / COMPILE-FAIL / RUN-INCOMPLETE
> verdicts. The shared deps it leans on (`vm/vmssh.sh`, the atlas engine
> `RunAtlasHeadless`, `atlas/consolidate-atlas.py`) live in the VM harness —
> see [12](12-ui-capture-atlas.md). `pr-pick.py` + the GitHub API path run anywhere;
> the on-VM steps need the [Windows-VM target](06-windows-vm.md).

A self-driving loop that, on a schedule, **reviews open upstream PRs** by stacking
the rig's local fix branch onto each one and measuring the result. Where the
[autonomous loop](09-autonomous-loop.md) drives *one* branch to find/fix bugs,
this one rotates through *every open PR* and answers: does this PR still let our
fixes apply, compile, boot, and render every screen? The mechanical part is
[`pr-review/pr-review-tick.sh`](../pr-review/pr-review-tick.sh); run it on a
schedule (cron / systemd timer / Claude `/loop`), ~25–35 min/tick.

## What one tick does

```
1. LOCK       take pr-review/.pr-review.lock; SKIP if the autonomous-fix loop
              holds its lock (they share the one VM). Release on every exit.
2. PREFLIGHT  VM running? SSH reachable? If not, report and exit. NEVER auto-boot.
3. PICK       pr-pick.py chooses the next open PR (rotation below), unauthenticated.
4. STACK      on the guest: fetch the PR head, cherry-pick $STACK_BASE..$FIX_BRANCH
              onto it (pr-stack.ps1). A conflict is a finding — write it and stop.
5. COMPILE    batchmode compile of PR+stack (reset-and-batchcompile.ps1). Definitive: no
              Safe-Mode modal, logs every `error CS`, and leaves assemblies WARM.
6. CAPTURE    warm full-atlas run (launch-atlas-warm.ps1) — reaches interactive,
              teleports to a quiet parcel, captures every UI surface. NO chat.
              Retries once if the editor stalls or writes a partial report.
7. RE-VERIFY  any screen broken beyond baseline → fast warm SUBSET recapture of
              just those screens, separating real regressions from flakiness.
8. PULL       scp the report + raw shots back; consolidate to <CODE>-<route>.png.
9. REVIEW     pr-write-review.py renders PR-<N>-review-<NN>.md (stack / compile /
              runtime / confirmed-regressions-vs-baseline / error triage).
10. RESTORE   kill the editor, return the guest to the fix branch; release the lock.
```

Steps 1–10 are all mechanical — unlike the autonomous loop, the *product here is
the written review*, so the tick runs end-to-end without a human in the middle.
The judgement (acting on a review) happens when you read the files afterward.

## Output

```
$DCL_REVIEW_OUT/                         (default ~/dcl-editor-pr-review/)
  PR-<N>-review-<NN>.md                   one review per (PR, tick)
  PR-<N>/<CODE>-<route>.png               the code-named screenshots for that PR
  PR-<N>/harness-report.json              the raw session report
  PR-<N>/batchcompile.log                 the compile log (and -errors.txt on fail)
```

Reviews are **append-only per PR**: each tick that re-reviews a PR writes the next
`-<NN>`. Screenshots overwrite (latest wins) so `PR-<N>/` is always the current set.

## PR rotation (`pr-pick.py`)

Picks the next PR over the **unauthenticated** GitHub API (the repo is public;
~1–2 requests/tick, far under the 60/hr anonymous limit — no token needed). PR
heads also fetch over the anonymous git protocol, so nothing authenticates on the
VM. Priority:

1. an open PR whose head SHA **changed** since its last review → re-review the push;
2. else the **least-recently-reviewed** open PR (never-reviewed sorts first);
3. ties break by PR number, for determinism.

Last-reviewed head + sequence number come from parsing the existing review files'
`head:` line — the review files *are* the loop's memory, so it's stateless across
hosts.

## Screen-health verdict (`pr-write-review.py`)

A screen broken this run is only a regression if it works at **baseline** — the set
already broken with our stack alone (no PR) on current dev. Don't hand-maintain that
set: **measure** it with [`pr-baseline.sh`](../pr-review/pr-baseline.sh) (full atlas
on the fix branch, no PR → broken routes written to `pr-review-baseline.txt`).
Re-measure after re-basing the stack: the atlas *drivers* drift against upstream UI
changes, so the baseline moves (e.g. `badgesdetail`/`passport` showed "broken"
purely because their drivers went stale on newer dev — *not* any PR's fault; a
hardcoded `camera`-only baseline would have flagged them as false regressions on
every PR).

**Confirm before flagging** — the key to a trustworthy verdict. A single atlas pass
is per-screen flaky, so a screen broken-beyond-baseline on the first pass is *not*
immediately a regression. The tick re-captures only those suspects via a fast warm
**subset recapture** ([`pr-broken.py`](../pr-review/pr-broken.py) finds them,
[`pr-recheck.py`](../pr-review/pr-recheck.py) classifies the recapture):
- **confirmed** (still broken after recapture) → real `REGRESSION`;
- **recovered** (passed on recapture) → flaky; reported, *not* a regression (and its
  PNG is refreshed with the good shot);
- **missing** (recapture itself didn't complete) → `INCONCLUSIVE`, never a false alarm.

A run that yields **zero** screen markers (editor stalled / partial report) is
`RUN-INCOMPLETE` — an environment hiccup, explicitly not a PR signal — and the atlas
step **retries once** before giving up. See the atlas hardness tiers in
[12](12-ui-capture-atlas.md).

## The guest scripts (why each exists)

The on-VM half reuses the [Windows VM](06-windows-vm.md) target's round-trips
(`vm/vmssh.sh`, scp) and the atlas *engine* (`RunAtlasHeadless`, which lives in
the VM harness — [12](12-ui-capture-atlas.md), not vendored here). Three small
PowerShell steps are PR-review-specific:

| Script | Why it's separate |
|--------|-------------------|
| [`pr-stack.ps1`](../pr-review/pr-stack.ps1) | Fetches the PR head and cherry-picks our fixes onto it **on the guest** (it already has the branch + warm caches; avoids a ~200MB bundle/tick). A conflict unwinds cleanly and is reported. |
| [`reset-and-batchcompile.ps1`](../pr-review/reset-and-batchcompile.ps1) | Batchmode compile = the *definitive* compile signal: a GUI compile error pops a blocking **"Enter Safe Mode?"** modal and just hangs with no logged error. Batchmode logs `error CS` and exits. Nuking `ScriptAssemblies`/`Bee` first gives a clean compile **and leaves them warm** for step 6. |
| [`launch-atlas-warm.ps1`](../pr-review/launch-atlas-warm.ps1) | Launches the atlas capture **without** nuking assemblies — if it recompiled during GUI startup it would hit the cold-recompile domain-reload hang. Reusing step 5's warm assemblies skips that. |

`reset-and-batchcompile.ps1` and `launch-atlas-warm.ps1` are piped over SSH to
`powershell -Command -` (no `param()`). **`pr-stack.ps1` is NOT** — it must run as
`powershell -File pr-stack.ps1 -PR <n>` (with a `param([string]$PR)` header).
**Learned the hard way:** when a git-heavy script is piped to `-Command -`, the
running `git` inherits that same stdin and *consumes the rest of the script*, so
everything after the first big `git` call (the `STACK=clean/conflict` result line)
silently never executes. `-File` gives git a clean stdin and fixes it. (The other
two scripts run no `git`, so piping them is fine.)

Each launches Unity via an **Interactive Scheduled Task** because an SSH command
lands in Windows session 0 (no graphics device — Unity won't even start there).
Same gotchas as [`09`](09-autonomous-loop.md#known-vm-gotchas).

## Guardrails (same spirit as the autonomous loop)

**Always** lock first (and yield to the fix loop's lock — one VM); release on
every exit; return the guest to the fix branch; write a review for *every*
outcome (conflict / compile-fail / clean) — a failed stack is a useful finding.

**Never** boot the VM; push; or let PR screenshots touch the curated
`atlas/shots/` (they go to their own per-PR dir via `DCL_ATLAS_OUT`).

## Config

All in [`config.sh`](../config.sh) under *PR-review loop*, env-overridable:

| Var | Default | Meaning |
|-----|---------|---------|
| `DCL_REVIEW_REPO_SLUG` | `decentraland/unity-explorer` | repo whose open PRs to review |
| `DCL_REVIEW_OUT` | `~/dcl-editor-pr-review` | where reviews + PNGs land |
| `DCL_REVIEW_FIX_BRANCH` | `auto-fixes` | local branch holding the fixes to stack |
| `DCL_REVIEW_STACK_BASE` | `our-stack-base` | ref the fixes sit on (`base..fix` = commits to pick) |
| `DCL_VM_EXPLORER` / `DCL_VM_UNITY_EXE` | `C:\Users\dcl\…` | guest checkout + Unity.exe |
| `DCL_REVIEW_EXPECTED_BROKEN` | `camera` | routes broken at baseline (not regressions) |

## Scheduling

Run every 30 min (or your cadence). A "good" tick: a PR picked, stacked, compiled
clean, reached interactive, ~62/63 screens captured, review written. Because the
tick yields to the autonomous-fix loop's lock, the two can share one cron/VM
without colliding.

## Operating notes (from the first 10 live reviews)

First 10 distinct PRs reviewed against the 44-commit stack:
**7 PASS · 1 INCONCLUSIVE · 2 CONFLICT.** PASS-with-screens settled at 60–62/63 with
the 3-screen baseline (`badgesdetail,camera,passport`) correctly subtracted — **zero
false regressions across all 10**, which is exactly what the measured baseline + the
confirm-before-flagging re-verify are for.

Two refinements the run surfaced (worth applying next):

1. **Empty cherry-pick is mislabeled `CONFLICT`.** When a PR already contains a commit
   equivalent to one in our stack (e.g. #8914 *is* the `--asset-bundles-url` feature),
   that commit cherry-picks **empty** → non-zero exit but **zero unmerged files**, so
   `pr-stack.ps1` reports `STACK=conflict files=(empty-or-unknown)`. That's a
   *redundancy* (the PR already carries one of our fixes), not a real conflict. Fix: on
   a failed pick with no `--diff-filter=U` files, `git cherry-pick --skip` and continue;
   only emit `CONFLICT` when files are genuinely unmerged. (#8914 and #8905 were both this.)

2. **Re-verify recapture is single-attempt.** If the *recapture* editor stalls (the same
   licensing/domain-reload flake the main atlas already retries through), the suspect
   can't be confirmed → `INCONCLUSIVE` (#6196). Safe — never a false regression — but
   extending the main run's retry-once to the recapture would turn more of these into a
   definitive confirmed/recovered verdict.

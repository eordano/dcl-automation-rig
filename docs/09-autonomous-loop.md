# Cross-cutting — the autonomous loop (runbook)

> **Status:** Not verified. The loop has not been run (the production loop is paused); design/runbook only.

A self-driving QA loop that, on a schedule, builds/runs/drives the Explorer in
the Windows VM, measures it, checks for regressions, and records findings —
accumulating memory across iterations. Distilled from ~100 hourly ticks on the
win11 rig. The mechanical part is [`loop/run-loop.sh`](../loop/run-loop.sh); the
judgement part (this doc) is the runbook the operator/agent follows.

## What one tick does

```
1. LOCK         take loop/.dcl-loop.lock (skip if a tick <90 min old holds it).
                ALWAYS release on exit, even on error.
2. STATE        read loop/dcl-loop-state.md first. If a PAUSE banner is present,
                write a "paused" note and exit — run nothing.
3. PREFLIGHT    VM running? SSH reachable? If not, report and exit. NEVER auto-boot.
4. SESSION      run vm/run-playtest.sh (reset → launch editor harness in session 1
                → poll → self-heal the two stall signatures → validate world reached).
5. CHECK        grep the session report for every sentinel in regression-checks.txt;
                all must be 0.
6. DECIDE       pick the highest-value fix (priority below). If nothing verifiable,
                report a clean tick and stop.
7. FIX + VERIFY make the minimal coherent change; re-run the relevant step; confirm
                the targeted issue is gone AND zero new errors appeared.
8. COMMIT       PR-sized commit to the auto-fixes branch. Never `git add -A`,
                never push, never touch generated *.gen.cs.
9. RECORD       write loop/logs/run-<UTC>.md; update dcl-loop-state.md; release lock.
```

Steps 1–5, 9 (mechanical) are automated by `run-loop.sh`. Steps 6–8 (judgement)
are yours — the script stops at a measured baseline so you start clean.

## Fix priority (step 6)

Work the highest tier that has a *verifiable* fix; skip the tick if none does.

1. **Compile** — import errors/warnings (goal: zero). Whole-file/assembly passes,
   not one-line band-aids. Only change a nullable-ref warning if you can reason
   the fix is correct and confirm zero new errors.
2. **Performance** — GC spikes, frame hitches, slow ECS systems, main-thread I/O.
   Measure with the harness; report before/after numbers in the commit.
3. **UX latency** — artificial waits, serial awaits that could be parallel,
   redundant reloads; especially **time-to-interactive** on the loading screen.
4. **Runtime quality** — NullRefs, lifecycle, cancellation bugs surfaced by
   playing Genesis Plaza. Skip *environmental* errors (HTTP 5xx, 403, audio).

## Guardrails

**Always**
- Lock first; release on every exit path.
- Commit only **verified** fixes (re-ran the step, issue gone, 0 new errors).
- Route every perf claim through the harness, baselined against prior runs.
- Stamp the run log and the state file each tick.

**Never**
- `git add -A` (≈100+ generated files are intentionally dirty) — stage named files.
- Push, or commit to any branch but `auto-fixes`.
- Change host/guest system, network, or firewall settings.
- Auto-boot the VM, or hold the interactive session >~50 min (finish, commit if
  verified, release, report).
- Blind-fix environmental errors, or "fix" content/platform issues as if code
  bugs — classify first (see [`08-testing.md`](08-testing.md) three buckets).

## Self-heal & stop conditions

- **Licensing/compile stall** (log frozen below the play threshold): the inner
  `run-playtest.sh` kills and relaunches up to N times with a full env reset.
- **VM down / SSH dead / sshd missing**: report and exit; next tick retries. (The
  original rig self-heals sshd once via an elevated GUI PowerShell; reproduce
  that only if you have a console path that doesn't touch system policy.)
- **All attempts fail on the same environmental stall**: record "environmental",
  release, exit — don't burn the budget.
- **Reboot dropped the session** (`explorer=0` over SSH, at the Windows login
  screen): skip the session rather than waste retries; auto-login must be
  arranged out-of-band.

## State carry (`dcl-loop-state.md`)

The loop's memory; read first, update last. Track: the healthy **baseline**
(reachedInteractive, expected errorCount + benign breakdown, TTI); **adopted
fixes** as sentinels (the string each bug produced → added to
`regression-checks.txt`); the **fix queue** in priority order; and a reverse-
chronological **outcome log** (`CLEAN N/N`, `INVALID`, `ENV-LIMITED`, `FIX …`,
`USER …`). Template: [`loop/dcl-loop-state.template.md`](../loop/dcl-loop-state.template.md).

## Scheduling

Run hourly (or your cadence) via cron / systemd timer / Claude `/loop`. Budget
~50 min/tick. A "good" tick: session reaches `Completed`, all checks 0, new
errors 0, and either one verified fix committed or a clean-tick report.

## Known VM gotchas (encoded in the scripts; repeated here so you don't relearn)

- **Session 0 vs 1.** SSH lands in session 0 (no graphics) — Unity's compiler
  never finishes there. Launch via an Interactive Scheduled Task (session 1).
- **UTF-8 BOM.** `File.WriteAllText` strips the BOM some `.cs` files carry,
  producing a phantom line-1 diff. Detect `EF BB BF` and write with
  `UTF8Encoding($hasBom)`; always `git diff` after patching.
- **Click drift.** The guest framebuffer (1280×800) can differ from the desktop;
  recalibrate the click mapping on a static screen before clicking Play.
- **Don't `git clean -fd` the harness dir** — it deletes untracked tooling. Use
  `GIT_LFS_SKIP_SMUDGE=1` for worktree checkouts (gltfast LFS outages).

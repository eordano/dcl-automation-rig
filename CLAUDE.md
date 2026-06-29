# CLAUDE.md

Guidance for AI agents working in this repo. Read [`README.md`](README.md) and
[`docs/00-shared.md`](docs/00-shared.md) first — this file is the *how to work
here*, not the *what it is*.

## What this repo is (and isn't)

A **consolidation of techniques** for building/running/driving the Decentraland
Unity Explorer headlessly across Linux, a Windows VM, and Mac. It is shell +
Python + a little C# **glue around tools you already have** — **nothing in this
repo compiles or ships a binary.** The Unity project it drives
(`$DCL_EXPLORER_REPO`) lives elsewhere; this rig points at it.

Most of it is **adapted from working source rigs and NOT re-verified on a clean
host.** Every doc carries a `Status:` banner; the README's verification ledger is
the ground truth for what has actually been observed working here. When you make
a claim about behavior, match that honesty — say "verified" only for what you ran.

## Architecture / mental model

- **`config.sh` is the single source of truth.** Unity version, project paths,
  build `-executeMethod` targets, harness entry points, ports, auth, VM paths,
  PR-review settings. **Every shell script sources it.** Everything is
  `: "${VAR:=default}"` — env-overridable, no hardcoded host is the only option.
  New facts that more than one script needs go *here*, not duplicated.
- **`lib/` is sourced, never run.** `common.sh` (logging/polling/scoped kills),
  `headless-display.sh` (the nested sway+wayvnc+Xwayland+audio stack),
  `auth.sh` (throwaway-wallet login). Keep them dependency-free and side-effect-free.
- **One dir per target** (`linux/ windows/ vm/ mac/`) + cross-cutting capability
  dirs (`loop/ pr-review/ bevy/ atlas/ analysis/ auth/`). Each holds only what's
  unique to it; shared facts live in `config.sh` / `docs/00-shared.md`.
- **`unity/` is C# you drop into the *other* repo** (`Explorer/Assets/Editor/`),
  not built here. `docs/` pairs one file per target + cross-cutting docs (00, 08–18).

## Conventions to follow when editing

- **One fact, one place.** Before hardcoding a path/version/port, check whether
  `config.sh` already has it (or should). Add a `: "${VAR:=…}"` + `export`.
- **Comments explain *why*, not *what*.** Every non-obvious trick carries the
  reason it exists (the bwrap `/tmp/.X11-unix` bind, session-0-vs-1, the warm-vs-
  cold assembly dance, `param()`-vs-prepend). That's the part you can't re-derive
  — preserve it. The C# in `unity/` encodes bugs that already bit; don't strip its comments.
- **Keep the `Status:` banner honest** when you touch a doc. If you verify
  something, update the README ledger with the evidence.
- **Match the surrounding style.** Bash: `set -uo pipefail` (or `-euo` for
  launchers), source config + common, `dcl_log`/`dcl_die`, `note()` to a runlog
  for loops. Python: stdlib-only where possible, docstring header, env-overridable.

## Running things

```bash
. ./config.sh                      # resolve + inspect every value
bash -n script.sh                  # syntax-check a shell script before running
python3 -m py_compile foo.py       # syntax-check Python
./linux/editor-up.sh               # bring up the headless display + launch editor
./pr-review/pr-review-tick.sh      # one PR-review tick (needs the VM up)
```

There's no test suite or build. Validate changes by syntax-checking, sourcing
`config.sh`, and (for Python tools) running them on mock/live data — e.g.
`pr-review/pr-pick.py` hits the live GitHub API; `pr-write-review.py` renders from a
fake report. The headless display, VM, and editor steps need a real
host/VM/GPU — see the README ledger for what can't run here.

## Gotchas that will bite you (encoded in the scripts — don't relearn)

- **Scoped process kills only.** Use `dcl_pkill_scoped` (matches on the rig's
  `XDG_RUNTIME_DIR`). A bare `pkill -f Decentraland` nukes every parallel rig.
- **NixOS needs the FHS wrap.** Unity/Wine are glibc ELFs; `DCL_FHS_WRAP` runs
  them through an FHS provider. Empty (no-op) on a normal distro.
- **Windows session 0 has no graphics device.** An SSH/WinRM command lands there;
  Unity (even `-batchmode`) won't reliably start. Launch via an **Interactive
  Scheduled Task** instead (see every `windows/*.ps1` and `pr-review/*.ps1`).
- **Piped PowerShell can't use `param()` when a var is prepended.** The
  `pr-review/*.ps1` take config as prepended `$Var='…'` (so `$PR` works); `param()`
  must be the first statement, so they use `if (-not $X) { $X = … }` defaults.
- **Warm vs cold assemblies.** The PR-review flow batch-compiles (nuke → warm)
  *before* the GUI atlas run (no nuke) to dodge the cold-recompile-during-startup
  domain-reload hang. Don't "simplify" by nuking in the atlas step.
- **`-nographics` is builds & EditMode tests only** — never PlayMode or the
  in-editor harness (they render). See `docs/00-shared.md`.

## When driving the autonomous / PR-review loops

- **Lock first, release on every exit.** Both loops use a lock with a TTL;
  `pr-review-tick.sh` also yields to the autonomous loop's lock (they share one VM).
- **Never auto-boot the VM**; preflight and exit if it's down.
- **Never `git add -A`** in the driven project (≈100+ generated files are
  intentionally dirty) — stage named files. **Never push**; commit only verified
  fixes to the designated branch. See [`docs/09`](docs/09-autonomous-loop.md).
- **Don't modify the external repos** the rig only *references*: the bevy-explorer
  harness (`bevy/` drives it read-only) and the VM-control harness that owns the
  atlas *engine* + PNGs (`atlas/` here is only the path-free naming layer — keep
  `atlas-codes.json` the source of truth and regenerate `INDEX.md` with `gen-index.py`).

## Don't commit

Rig-local state (see [`.gitignore`](.gitignore)): `.dcl-rig/`, `vm/reports/`,
`loop/` live state + logs + lock, `pr-review/` lock + output, `*.log`,
`__pycache__/`, `atlas/shots/`, and **never** a wallet or secret (`*wallet*.json`).

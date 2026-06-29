#!/usr/bin/env bash
# =============================================================================
# pr-review-tick.sh — one PR-review tick, REWRITTEN for the web stack.
#
# The Unity rig's tick drove a Windows VM: scp pr-stack.ps1 to the guest, rebase
# + cherry-pick our 44 commits there, Unity batchmode compile, VM warm atlas
# capture, scp the report+PNGs back. ALL of that (pr-stack.ps1, the VM, SSH, the
# .ps1 helpers) is DROPPED. The platform-agnostic ORCHESTRATION is ported:
#   pick a PR (pr-pick.py rotation) → rebase-on-base + cherry-pick our fixes with
#   empty-skip → in-depth code review (headless claude, concurrent) →
#   COMPILE + CAPTURE → re-verify suspects (confirm-before-flag) → write review.
#
# REWRITTEN steps (the web-stack substance):
#   - stack   = git rebase onto fresh base + cherry-pick $BASE..$FIX with
#               `git cherry-pick --skip` on empty (a fix already in the PR).
#   - compile = cargo check/build bevy-explorer + tsc/build sites/app +
#               cargo check catalyst (the three web-stack compile units).
#   - capture = rig/atlas/url-walk.sh (HUD routes) + rig/bevy/product-tour-web.sh
#               (wasm functional surface) — measured-baseline, run on a fresh stack.
#
# Guardrails (ported): never push; own lock; skip if the auto-fix loop lock is
# held; confirm-before-flag re-verify of broken-beyond-baseline screens.
# Retarget DCL_REVIEW_REPO_SLUG at the web-stack repos. Long; run backgrounded.
# =============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/../config.sh" 2>/dev/null || true
. "$HERE/../lib/common.sh" 2>/dev/null || true

SLUG="${DCL_REVIEW_REPO_SLUG:-decentraland/bevy-explorer}"
CHECKOUT="${DCL_REVIEW_CHECKOUT:-${DCL_BEVY_REPO:-$HOME/projects/bevy-explorer}}"
REVIEW_DIR="${DCL_REVIEW_OUT:-$HOME/dcl-pr-review}"
FIX_BRANCH="${DCL_REVIEW_FIX_BRANCH:-auto-fixes}"
STACK_BASE="${DCL_REVIEW_STACK_BASE:-our-stack-base}"
BASEBRANCH="${DCL_REVIEW_BASE_BRANCH:-main}"
REMOTE="${DCL_PR_REMOTE:-origin}"
LOG_DIR="$REVIEW_DIR/_logs"; mkdir -p "$LOG_DIR"
LOCK="$HERE/.pr-review.lock"; LOCK_TTL="${PR_REVIEW_TTL:-2700}"   # 45 min
AUTOFIX_LOCK="$HERE/../loop/.dcl-loop.lock"
UTC="$(date -u +%Y%m%dT%H%M%SZ)"
log(){ printf '[%s] %s\n' "$(date -Iseconds)" "$*"; }

# ---- preflight ----
[ -d "$CHECKOUT/.git" ] || { log "no checkout at $CHECKOUT (set DCL_REVIEW_CHECKOUT/DCL_BEVY_REPO)"; exit 0; }
if [ -f "$AUTOFIX_LOCK" ]; then
  age=$(( $(date +%s) - $(stat -c %Y "$AUTOFIX_LOCK" 2>/dev/null || echo 0) ))
  [ "$age" -lt 5400 ] && { log "auto-fix loop busy — skip"; exit 0; }
fi
if [ -f "$LOCK" ]; then
  age=$(( $(date +%s) - $(stat -c %Y "$LOCK" 2>/dev/null || echo 0) ))
  [ "$age" -lt "$LOCK_TTL" ] && { log "another pr-review tick running (${age}s) — skip"; exit 0; }
  log "stale lock (${age}s) — taking over"
fi
echo "$$ $UTC" > "$LOCK"; trap 'rm -f "$LOCK"' EXIT

# ---- pick a PR ----
pick="$(PR_REPO="$SLUG" PR_REVIEW_DIR="$REVIEW_DIR" python3 "$HERE/pr-pick.py")"; pprc=$?
if [ "$pprc" -ne 0 ]; then
  if [ "$pprc" -eq 3 ]; then log "all open PRs already reviewed — nothing to do"
  else log "pr-pick: no PR to review (rc=$pprc — API rate-limit or no open PRs); skipping"; fi
  exit 0
fi
IFS=$'\t' read -r PR_NUM PR_HEADSHA PR_HEADREF REVIEW_PATH PR_TITLE <<<"$pick"
SHOT_DIR="$REVIEW_DIR/PR-$PR_NUM"; mkdir -p "$SHOT_DIR"
log "reviewing PR #$PR_NUM ($PR_HEADREF) -> $(basename "$REVIEW_PATH")"
export PR_NUM PR_TITLE PR_HEAD="$PR_HEADSHA" SHOT_DIR REVIEW_PATH UTC \
       PR_REVIEW_DIR="$REVIEW_DIR" DCL_REVIEW_FIX_BRANCH="$FIX_BRANCH"

# ---- in-depth code review (host, headless claude per the project rubric) launched
#      NOW so it runs concurrently with stack/compile/capture. writereview() waits. ----
export CODEREVIEW_FILE="$SHOT_DIR/_codereview.md"
( DCL_REVIEW_REPO_SLUG="$SLUG" DCL_REVIEW_CHECKOUT="$CHECKOUT" DCL_REVIEW_BASE_BRANCH="$BASEBRANCH" \
    "$HERE/pr-codereview.sh" "$PR_NUM" "$CODEREVIEW_FILE" > "$LOG_DIR/PR-$PR_NUM-codereview-$UTC.log" 2>&1 ) &
CR_PID=$!
log "code review launched (pid $CR_PID)"
writereview(){ wait "$CR_PID" 2>/dev/null || true; python3 "$HERE/pr-write-review.py" "${1:-}"; }

# ---- stack: rebase PR head onto fresh base, then cherry-pick our fixes with empty-skip ----
log "stacking on $CHECKOUT (rebase $PR_HEADREF onto $BASEBRANCH + cherry-pick $STACK_BASE..$FIX_BRANCH)"
cd "$CHECKOUT" || { export STACK_STATUS=error STACK_FILES="no checkout"; writereview; exit 1; }
GIT_LFS_SKIP_SMUDGE=1 git fetch -q "$REMOTE" "$BASEBRANCH" 2>/dev/null || true
GIT_LFS_SKIP_SMUDGE=1 git fetch -q "$REMOTE" "pull/$PR_NUM/head" 2>/dev/null || { export STACK_STATUS=error STACK_FILES="pr fetch failed"; writereview; exit 1; }
prhead="$(git rev-parse FETCH_HEAD)"
WORK="pr-review/$PR_NUM"
git checkout -f -B "$WORK" "$prhead" >/dev/null 2>&1 || { export STACK_STATUS=error STACK_FILES="checkout failed"; writereview; exit 1; }

# rebase onto fresh base
if ! git rebase "$REMOTE/$BASEBRANCH" >/dev/null 2>&1; then
  conf="$(git diff --name-only --diff-filter=U 2>/dev/null | paste -sd, -)"
  git rebase --abort >/dev/null 2>&1 || true
  export STACK_STATUS=rebase-conflict STACK_FILES="$conf"
  log "PR does not rebase onto $BASEBRANCH — code review only, skipping capture"
  writereview; exit 0
fi

# cherry-pick our fix range with empty-skip (a fix already in the PR -> --skip)
skipped=0
if git rev-parse --verify -q "$STACK_BASE" >/dev/null && git rev-parse --verify -q "$FIX_BRANCH" >/dev/null; then
  for c in $(git rev-list --reverse "$STACK_BASE..$FIX_BRANCH" 2>/dev/null); do
    if ! git cherry-pick -x "$c" >/dev/null 2>&1; then
      if git diff --cached --quiet 2>/dev/null && git diff --quiet 2>/dev/null; then
        git cherry-pick --skip >/dev/null 2>&1; skipped=$((skipped+1)); continue   # empty = already in PR
      fi
      conf="$(git diff --name-only --diff-filter=U 2>/dev/null | paste -sd, -)"
      git cherry-pick --abort >/dev/null 2>&1 || true
      export STACK_STATUS=conflict STACK_FILES="$conf"
      writereview; exit 0
    fi
  done
else
  log "no $STACK_BASE..$FIX_BRANCH range present — reviewing PR head alone (no fixes to stack)"
fi
export STACK_STATUS=clean STACK_TIP="$(git rev-parse HEAD)" STACK_SKIPPED="$skipped"
log "stack clean (tip $(git rev-parse --short HEAD), skipped=$skipped)"

# ---- compile: cargo bevy + tsc sites + cargo catalyst ----
log "compile: cargo (bevy) + tsc (sites) + cargo (catalyst)"
CLOG="$LOG_DIR/PR-$PR_NUM-compile-$UTC.log"; : > "$CLOG"
SH="${DCL_SHELL:-dcl-shell}"
cfail=0
{
  echo "=== cargo check bevy-explorer ($CHECKOUT) ==="
  "$SH" -c "cd '$CHECKOUT' && cargo check --quiet" 2>&1 || cfail=1
  if [ -d "${DCL_SITES_REPO:-}" ]; then
    echo "=== tsc sites/app (${DCL_SITES_REPO}) ==="
    "$SH" -c "cd '$DCL_SITES_REPO' && (npx --no-install tsc -p tsconfig.json --noEmit || npm run -s typecheck)" 2>&1 || cfail=1
  else
    echo "=== sites/app SKIPPED (DCL_SITES_REPO unset/missing) ==="
  fi
  if [ -d "${DCL_CATALYST_REPO:-}" ]; then
    echo "=== cargo check catalyst (${DCL_CATALYST_REPO}) ==="
    "$SH" -c "cd '$DCL_CATALYST_REPO' && cargo check --quiet" 2>&1 || cfail=1
  else
    echo "=== catalyst SKIPPED (DCL_CATALYST_REPO unset/missing) ==="
  fi
} >> "$CLOG" 2>&1
# Count real compiler errors (rust `error[..]:`/`error:` + ts `error TS`).
cs=$(grep -cE 'error(\[|:)|error TS' "$CLOG" 2>/dev/null); cs=$(printf '%s' "${cs:-0}" | tr -d '\n ')
if [ "$cfail" -ne 0 ] || [ "${cs:-0}" -gt 0 ]; then
  export COMPILE_STATUS=fail COMPILE_ERRORS="$CLOG"
  log "compile FAIL (${cs:-0} error lines)"; writereview; exit 0
fi
export COMPILE_STATUS=ok
log "compile clean"

# ---- capture: atlas URL-walk + web product tour (measured baseline) ----
log "capture: atlas URL-walk + web product tour"
CAP_REPORT="$SHOT_DIR/_report.json"
# url-walk produces named PNGs + a report; product-tour adds the wasm functional
# ledger. Both run on the rig's GPU sway. If neither produces a report, the run
# was incomplete (RUN-INCOMPLETE branch in pr-write-review.py).
DCL_ATLAS_AUTH=anon "$HERE/../atlas/url-walk.sh" "$SHOT_DIR" >> "$LOG_DIR/PR-$PR_NUM-capture-$UTC.log" 2>&1 || true
DCL_SKIP_READY=1 "$HERE/../bevy/product-tour-web.sh" "$SHOT_DIR/_tour" >> "$LOG_DIR/PR-$PR_NUM-capture-$UTC.log" 2>&1 || true

# Build a minimal capture report (actions[].label=atlas_<route>, error="shown") from
# the consolidated PNGs so pr-write-review.py / pr-recheck.py read the same shape.
python3 - "$SHOT_DIR" "$CAP_REPORT" <<'PY'
import glob, json, os, sys
shot_dir, out = sys.argv[1], sys.argv[2]
acts = []
for p in sorted(glob.glob(os.path.join(shot_dir, "*.png"))):
    base = os.path.basename(p)[:-4]
    route = base.split("-", 1)[1] if "-" in base else base
    acts.append({"label": f"atlas_{route}", "error": "shown"})
json.dump({"actions": acts}, open(out, "w"))
print(f"capture report: {len(acts)} screen markers -> {out}")
PY

acts=$(python3 "$HERE/pr-count.py" "$CAP_REPORT" 2>/dev/null); acts=$(printf '%s' "${acts:-0}" | tr -d '\n ')
log "capture markers: ${acts:-0}"

if [ "${acts:-0}" -gt 0 ]; then
  # ---- confirm-before-flag: re-verify broken-beyond-baseline screens ----
  suspects="$(python3 "$HERE/pr-broken.py" "$CAP_REPORT" 2>/dev/null)"
  if [ -n "$suspects" ]; then
    log "suspected regressions: $suspects — re-verifying (subset recapture)"
    # Re-walk only the suspect routes (url-walk honors a route subset via env).
    DCL_ATLAS_AUTH=anon DCL_ATLAS_ONLY="$suspects" "$HERE/../atlas/url-walk.sh" "$SHOT_DIR/_recap" \
      >> "$LOG_DIR/PR-$PR_NUM-recap-$UTC.log" 2>&1 || true
    RECAP="$SHOT_DIR/_recap.json"
    python3 - "$SHOT_DIR/_recap" "$RECAP" <<'PY'
import glob, json, os, sys
d, out = sys.argv[1], sys.argv[2]; acts=[]
for p in sorted(glob.glob(os.path.join(d, "*.png"))):
    base=os.path.basename(p)[:-4]; route=base.split("-",1)[1] if "-" in base else base
    acts.append({"label": f"atlas_{route}", "error": "shown"})
json.dump({"actions": acts}, open(out,"w"))
PY
    if [ -s "$RECAP" ]; then
      eval "$(python3 "$HERE/pr-recheck.py" "$RECAP" "$suspects")"
      export REVERIFIED=1 CONFIRMED_REGRESS="${confirmed:-}" FLAKY_RECOVERED="${recovered:-}" INCONCLUSIVE="${missing:-}"
      log "re-verify: confirmed=[${confirmed:-}] recovered=[${recovered:-}] missing=[${missing:-}]"
    else
      export REVERIFIED=1 CONFIRMED_REGRESS="" FLAKY_RECOVERED="" INCONCLUSIVE="$suspects"
      log "re-verify recapture incomplete — suspects inconclusive"
    fi
  fi
  writereview "$CAP_REPORT"
else
  log "capture incomplete (0 markers)"
  writereview   # RUN-INCOMPLETE branch (compile clean; capture stalled/partial)
fi

# ---- cleanup: return the checkout to the base branch (no leftover work branch) ----
git checkout -f "$BASEBRANCH" >/dev/null 2>&1 || git checkout -f "$REMOTE/$BASEBRANCH" >/dev/null 2>&1 || true
git branch -D "$WORK" >/dev/null 2>&1 || true
log "done PR #$PR_NUM (NEVER pushed)"

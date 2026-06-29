#!/usr/bin/env bash
# pr-codereview.sh <PR> <out.md> — in-depth LOCAL code review of a PR's diff using
# the project's OWN rubric (.github/prompts/review-instructions.md if present, else
# the repo CLAUDE.md) via headless `claude -p`. PORTED from the Unity rig; runs
# anywhere. The ONLY changes: it targets the web-stack repo under review
# (DCL_REVIEW_CHECKOUT, default the bevy-explorer checkout) and DCL_REVIEW_REPO_SLUG.
# LOCAL ONLY: writes the review to <out.md>. No GitHub, no inline comments, no push.
set -uo pipefail
PR="${1:?usage: pr-codereview.sh <PR> <out.md>}"; OUT="${2:?out.md}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$HERE/../config.sh" 2>/dev/null || true

SLUG="${DCL_REVIEW_REPO_SLUG:-decentraland/bevy-explorer}"
CHECKOUT="${DCL_REVIEW_CHECKOUT:-${DCL_BEVY_REPO:-$HOME/projects/bevy-explorer}}"
cd "$CHECKOUT" || { echo "(no checkout at $CHECKOUT)" > "$OUT"; exit 1; }

# fetch dev (or main) and the PR head SEPARATELY — fetching both in one command
# leaves FETCH_HEAD ambiguous. REMOTE defaults to 'origin'; DCL_PR_REMOTE may be a
# bare GitHub URL when origin can't serve pull/*/head. Capture each SHA so the diff
# base works for both a tracking ref and a URL fetch.
REMOTE="${DCL_PR_REMOTE:-origin}"
BASEBRANCH="${DCL_REVIEW_BASE_BRANCH:-main}"
GIT_LFS_SKIP_SMUDGE=1 git fetch -q "$REMOTE" "$BASEBRANCH" 2>/dev/null \
  || GIT_LFS_SKIP_SMUDGE=1 git fetch -q "$REMOTE" dev 2>/dev/null \
  || { echo "($BASEBRANCH/dev fetch failed)" > "$OUT"; exit 1; }
devbase="$(git rev-parse FETCH_HEAD)"
GIT_LFS_SKIP_SMUDGE=1 git fetch -q "$REMOTE" "pull/$PR/head" 2>/dev/null || { echo "(fetch failed)" > "$OUT"; exit 1; }
prhead="$(git rev-parse FETCH_HEAD)"
diff="/tmp/pr-$PR-codereview.diff"
git diff "$devbase...$prhead" > "$diff" 2>/dev/null   # 3-dot: the PR's OWN changes vs base (merge-base)
files=$(git diff --name-only "$devbase...$prhead" 2>/dev/null | wc -l)
dlines=$(wc -l < "$diff")
if [ "${files:-0}" -eq 0 ]; then echo "(empty diff vs $BASEBRANCH — PR head $prhead may already be merged)" > "$OUT"; echo "codereview PR#$PR: empty diff"; exit 0; fi

prompt="You are reviewing $SLUG PR #$PR on a LOCAL checkout. Do NOT use GitHub, do NOT post comments, do NOT push — write your review to stdout only.
Read .github/prompts/review-instructions.md (the rubric) if present, and CLAUDE.md (the standards), plus the relevant docs/ subsystem doc for what the diff touches. The PR's full diff vs $BASEBRANCH is the file $diff — read it. Review ONLY the changes in that diff file (open other repo files for CONTEXT only); do NOT review any other branch, and do NOT describe the auto-fixes branch.
Then analyze how the change works and review it in depth. Output markdown:
## Analysis — what the PR does and how it works (2-5 sentences)
## Root cause — what problem it solves; does it fix the cause or a symptom?
## Blocking issues — ONLY issues needing a fix: Location (file:line) / Problem / Fix / Why, citing the CLAUDE.md rule. No praise, no nits. If none, say 'None.'
Then end with EXACTLY these four lines (downstream parses them):
REVIEW_RESULT: PASS ✅
COMPLEXITY: SIMPLE
COMPLEXITY_REASON: <one sentence on which subsystem(s) the diff touches>
QA_REQUIRED: YES
(use FAIL/COMPLEX/NO where appropriate per the rubric)."

timeout 1200 claude -p "$prompt" --allowedTools "Read Grep Glob" > "$OUT" 2>/dev/null
rc=$?
rm -f "$diff"
[ -s "$OUT" ] || echo "(code review produced no output; claude rc=$rc)" > "$OUT"
echo "codereview PR#$PR -> $OUT ($files files / $dlines diff lines / claude rc=$rc)"

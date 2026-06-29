#!/usr/bin/env python3
# pr-pick.py — choose the next open PR to review, rotating for coverage.
# PORTED from the Unity rig; the only change is the default repo slug (retargeted
# off decentraland/unity-explorer onto the web-stack repos via DCL_REVIEW_REPO_SLUG
# / PR_REPO) and the review-file prefix. The rotation logic — unauthenticated
# GitHub PR listing, head-SHA-change detection, review-files-as-memory — is
# unchanged.
#
# Lists open non-draft PRs (unauthenticated GitHub API), then picks:
#   1. PRs never reviewed (prefer most-recently-updated), else
#   2. the open PR whose head SHA differs from its last review (re-review on push), else
#   3. the PR whose newest review file is oldest (round-robin re-review).
# Prints one TSV line:  <number>\t<head_sha>\t<head_ref>\t<review_path>\t<title>
# Exits 2 if no open PRs; 3 if all open PRs already reviewed.
import json, os, sys, urllib.request, glob, re

REPO = os.environ.get("PR_REPO", os.environ.get("DCL_REVIEW_REPO_SLUG", "decentraland/bevy-explorer"))
OUT  = os.environ.get("PR_REVIEW_DIR", os.environ.get("DCL_REVIEW_OUT", os.path.expanduser("~/dcl-pr-review")))

def api(path):
    req = urllib.request.Request(f"https://api.github.com/repos/{REPO}/{path}",
                                 headers={"Accept": "application/vnd.github+json",
                                          "User-Agent": "dcl-pr-review-loop"})
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.load(r)

prs = []
for page in range(1, 4):  # up to 300 open PRs
    batch = api(f"pulls?state=open&per_page=100&page={page}&sort=updated&direction=desc")
    prs += batch
    if len(batch) < 100:
        break
prs = [p for p in prs if not p.get("draft")]
if not prs:
    sys.exit(2)

def reviews_for(n):
    return sorted(glob.glob(os.path.join(OUT, f"PR-{n}-review-*.md")))

def last_reviewed_head(n):
    revs = reviews_for(n)
    if not revs:
        return None, 0.0
    newest = revs[-1]
    head = None
    try:
        with open(newest, encoding="utf-8", errors="replace") as f:
            for line in f:
                m = re.search(r"head[_ ]?sha\W*([0-9a-f]{7,40})", line, re.I)
                if m:
                    head = m.group(1); break
    except OSError:
        pass
    return head, os.path.getmtime(newest)

# Review each PR exactly once: pick only never-reviewed open PRs (most-recently-updated first).
never = [p for p in prs if not reviews_for(p["number"])]
if not never:
    sys.exit(3)                                       # all open PRs already reviewed — nothing to do
pick = never[0]

n = pick["number"]
review_path = os.path.join(OUT, f"PR-{n}-review-01.md")
title = pick["title"].replace("\t", " ").replace("\n", " ")
print(f'{n}\t{pick["head"]["sha"]}\t{pick["head"]["ref"]}\t{review_path}\t{title}')

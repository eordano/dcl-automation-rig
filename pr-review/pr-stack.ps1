param([string]$PR)
# pr-stack.ps1 — on the guest: clean slate, fetch a PR head, stack our 44 commits on it.
# Run via:  powershell -NoProfile -File C:\Users\dcl\pr-stack.ps1 -PR <number>
# (Executed as a FILE, not piped to -Command -, so git children can't consume the script.)
# Prints:  PR_HEAD=<sha>  then  STACK=clean tip=<sha> | STACK=conflict files=<...> | STACK=error <msg>
if (-not $PR) { Write-Output 'STACK=error no-PR'; exit 1 }
$ErrorActionPreference = 'Continue'
$env:GIT_LFS_SKIP_SMUDGE = '1'
$env:GIT_TERMINAL_PROMPT = '0'
Set-Location C:/Users/dcl/unity-explorer

git cherry-pick --abort 2>$null | Out-Null
git rebase --abort 2>$null | Out-Null
git checkout -f auto-fixes 2>$null | Out-Null
git reset --hard auto-fixes 2>$null | Out-Null
git branch -D pr-stack 2>$null | Out-Null

git fetch origin "pull/$PR/head" 2>$null | Out-Null
$head = "$(git rev-parse --short FETCH_HEAD)".Trim()
Write-Output "PR_HEAD=$head"

git checkout -B pr-stack FETCH_HEAD 2>$null | Out-Null
git cherry-pick our-stack-base..auto-fixes 2>$null | Out-Null
$rc = $LASTEXITCODE

if ($rc -eq 0) {
    $tip = "$(git rev-parse --short HEAD)".Trim()
    Write-Output "STACK=clean tip=$tip"
} else {
    $u = ("$(git diff --name-only --diff-filter=U)").Trim() -replace '\s+', ','
    if (-not $u) { $u = '(empty-or-unknown)' }
    Write-Output "STACK=conflict files=$u"
    git cherry-pick --abort 2>$null | Out-Null
    git checkout -f auto-fixes 2>$null | Out-Null
}

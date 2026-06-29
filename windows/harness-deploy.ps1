# =============================================================================
# harness-deploy.ps1 — Windows twin of lib/common.sh dcl_harness_deploy.
# Drops the rig's canonical editor harness into the driven Unity project as
# COPIES (never symlinks). Absolute-path symlinks committed into the client repo
# are what broke the canonical build off-Linux; copies are OS-agnostic and can't
# dangle. Source of truth: <rig>/unity/*.cs. Target: a dedicated subfolder with a
# self-ignoring .gitignore so the client repo can never track/commit it.
# Relative/param-resolved paths only — no hardcoded host paths. Idempotent.
#
#   pwsh windows/harness-deploy.ps1                       # deploy (uses $env:DCL_PROJECT_DIR)
#   pwsh windows/harness-deploy.ps1 -ProjectDir C:\...\Explorer
#   pwsh windows/harness-deploy.ps1 -Remove              # undeploy
# Call it from reset-and-launch-editor.ps1 before launching Unity.
# =============================================================================
param(
  [string]$RigRepo    = (Split-Path -Parent $PSScriptRoot),   # rig repo = parent of windows/
  [string]$ProjectDir = $env:DCL_PROJECT_DIR,
  [switch]$Remove
)
$ErrorActionPreference = 'Stop'
if (-not $ProjectDir) { throw "ProjectDir not set (pass -ProjectDir or set DCL_PROJECT_DIR)" }
$dst = Join-Path $ProjectDir 'Assets\Editor\DclHarness'

if ($Remove) {
  Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $dst, ($dst + '.meta')
  Write-Host "harness undeployed from $ProjectDir"
  return
}

$src = Join-Path $RigRepo 'unity'
if (-not (Test-Path $src)) { throw "no harness source $src" }
if (-not (Test-Path (Join-Path $ProjectDir 'Assets'))) { throw "not a Unity project: $ProjectDir" }
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item -Force (Join-Path $src '*.cs') $dst
Set-Content -Path (Join-Path $dst '.gitignore') -Value '*'   # '*' ignores everything incl. itself
$n = (Get-ChildItem $dst -Filter *.cs).Count
Write-Host "harness deployed: $n .cs -> $dst (copies, gitignored)"

# =============================================================================
# run-tests.ps1 — run Unity EditMode/PlayMode tests in batchmode on Windows.
# Mirrors the Makefile CI flags so local runs match CI exactly.
#
#   .\windows\run-tests.ps1 -Mode editmode
#   .\windows\run-tests.ps1 -Mode playmode -Filter DCL.Tests.CodeConventionsTests
# =============================================================================
param(
  [ValidateSet("editmode","playmode")] [string]$Mode = "editmode",
  [string]$Project = "$env:USERPROFILE\unity-explorer\Explorer",
  [string]$Unity,
  [string]$Filter,
  [string]$Category = "!Performance",
  [string]$Results = "$env:USERPROFILE\test-results"
)
$ErrorActionPreference = "Stop"

if (-not $Unity) {
  $pv = Get-Content "$Project\ProjectSettings\ProjectVersion.txt" -Raw
  $v  = ([regex]::Match($pv, 'm_EditorVersion: ([0-9a-f.]+)')).Groups[1].Value
  $Unity = "C:\Program Files\Unity\Hub\Editor\$v\Editor\Unity.exe"
}
New-Item -ItemType Directory -Force -Path $Results | Out-Null

# Note: avoid the automatic $args variable name — use $uargs.
$uargs = @(
  "-batchmode",
  "-projectPath","`"$Project`"",
  "-runTests","-testPlatform",$Mode,
  "-testCategory","`"$Category`"",
  "-burst-disable-compilation","-accept-apiupdate",
  "-testResults","`"$Results\$Mode.xml`"",
  "-logFile","`"$Results\$Mode.log`""
)
# -nographics is safe for EditMode but NOT for PlayMode (Play mode + rendering
# need a graphics device — running it -nographics gives flaky/false results).
if ($Mode -eq "editmode") { $uargs = @("-nographics") + $uargs }
if ($Filter) { $uargs += @("-testFilter","`"$Filter`"") }

$p = Start-Process -FilePath $Unity -Wait -PassThru -ArgumentList $uargs
Write-Output "EXIT $($p.ExitCode)  results=$Results\$Mode.xml"
exit $p.ExitCode

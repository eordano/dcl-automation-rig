# =============================================================================
# build-player.ps1 — build the Windows player from the editor in batchmode.
# Target: "windows binary" (build half). Run on a Windows box with the editor
# installed. Calls the same unity/BuildScript entry point used on every platform.
#
#   .\windows\build-player.ps1 -Dev          # development build
#   .\windows\build-player.ps1 -GfxApi d3d12 # force a graphics API
# =============================================================================
param(
  [string]$Project = "$env:USERPROFILE\unity-explorer\Explorer",
  [string]$UnityVersion,                                   # auto-read from project if omitted
  [string]$Unity,                                          # full path to Unity.exe (overrides version lookup)
  [switch]$Dev,
  [string]$GfxApi,                                         # vulkan|gl|d3d11|d3d12 (optional)
  [string]$Out,                                            # output exe path (optional)
  [string]$Version = "0.0.0-dev"
)
$ErrorActionPreference = "Stop"

if (-not $UnityVersion) {
  $pv = Get-Content "$Project\ProjectSettings\ProjectVersion.txt" -Raw
  $UnityVersion = ([regex]::Match($pv, 'm_EditorVersion: ([0-9a-f.]+)')).Groups[1].Value
}
if (-not $Unity) { $Unity = "C:\Program Files\Unity\Hub\Editor\$UnityVersion\Editor\Unity.exe" }
if (-not (Test-Path $Unity)) { throw "Unity not found at $Unity" }

$method = if ($Dev) { "BuildScript.BuildWindows64Dev" } else { "BuildScript.BuildWindows64" }
$log = "$env:USERPROFILE\build-windows.log"

# BuildScript reads these from the environment (see unity/BuildScript.cs).
$env:DCL_BUILD_VERSION = $Version
if ($Out)    { $env:DCL_BUILD_OUT = $Out }
if ($GfxApi) { $env:DCL_GFX_API   = $GfxApi }

Write-Output "Unity:   $Unity"
Write-Output "Method:  $method"
Write-Output "Log:     $log"

# -quit after the method returns; -batchmode -nographics = headless build.
$p = Start-Process -FilePath $Unity -Wait -PassThru -ArgumentList @(
  "-batchmode","-nographics","-quit",
  "-projectPath","`"$Project`"",
  "-executeMethod",$method,
  "-buildTarget","Win64",
  "-logFile","`"$log`""
)
Write-Output "EXIT $($p.ExitCode)"   # 0 ok / 1 build failed / 3 exception (from BuildScript)
exit $p.ExitCode

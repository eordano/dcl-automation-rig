# =============================================================================
# reset-and-launch-editor.ps1 — clean-slate launch of the editor running an
# automation harness method. Target: "windows editor".
#
# The two non-obvious tricks this encodes:
#   1. CACHE NUKE: stale ScriptAssemblies/Bee/StateCache/Temp cause domain-reload
#      hangs and phantom compile errors on a fresh boot. Delete them first.
#   2. SCHEDULED TASK w/ Interactive logon: a command sent over SSH/WinRM lands
#      in session 0, which has NO graphics device — Unity Play mode and rendering
#      silently misbehave there. Launching via a Scheduled Task with
#      LogonType=Interactive runs it in the real desktop session (1) instead.
#      Start Unity Hub first so the licensing client is warm before the editor.
#
# Override paths via params; sensible VM defaults are baked in.
# =============================================================================
param(
  [string]$Project = 'C:\Users\dcl\unity-explorer\Explorer',
  [string]$Unity   = 'C:\Users\dcl\UnityEditors\6000.4.0f1\Editor\Unity.exe',
  [string]$Method  = 'DCL.Harness.DclPlaytestHarness.RunHeadless',
  [string]$Log     = 'C:\Users\dcl\harness-run.log',
  [string]$User    = 'dcl',
  [string]$TaskName = 'DclUnityHarness'
)
$ErrorActionPreference = 'SilentlyContinue'

# 1. Kill everything Unity-related and let handles release.
Get-Process -Name 'Unity','Unity Hub','Unity.Licensing.Client','UnityShaderCompiler',
  'bee_backend','UnityCrashHandler64','Decentraland' -EA SilentlyContinue | Stop-Process -Force
Start-Sleep 8

# 2. Nuke caches that break a cold boot.
foreach ($d in 'Library\ScriptAssemblies','Library\Bee','Library\StateCache','Temp') {
  Remove-Item (Join-Path $Project $d) -Recurse -Force -EA SilentlyContinue
}

# 3. Warm the licensing client (start Hub, give it time to authorize).
Start-Process 'C:\Program Files\Unity Hub\Unity Hub.exe' -WindowStyle Minimized
Start-Sleep 15

# 4. Clear prior outputs so polling can't read stale results.
Remove-Item $Log,'C:\Users\dcl\harness-report.json' -Force -EA SilentlyContinue

# 5. Launch the editor in the interactive desktop session via a Scheduled Task.
$arg  = "-projectPath `"$Project`" -executeMethod $Method -logFile `"$Log`""
$act  = New-ScheduledTaskAction -Execute $Unity -Argument $arg
$prin = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive
Register-ScheduledTask -TaskName $TaskName -Action $act -Principal $prin -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
Write-Output 'EDITOR_LAUNCHED'

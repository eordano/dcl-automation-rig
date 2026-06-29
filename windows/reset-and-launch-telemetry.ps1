# =============================================================================
# reset-and-launch-telemetry.ps1 — clean-slate launch of ONE telemetry/benchmark
# harness mode in the editor. Target: "windows editor".
#
# Same two tricks as reset-and-launch-editor.ps1 (cache nuke + Scheduled Task
# with Interactive logon so Play mode gets a real desktop/graphics device — see
# that file's header), parameterized by $Mode so all four measurement entry
# points share one launcher.
#
# Driven by vm/run-telemetry.sh, which pipes this in with `$Mode` pre-assigned
# (a piped script can't take -params). $Mode falls back to 'perf' if unset.
# Override the paths by pre-assigning $Project/$Unity/$User the same way.
# =============================================================================
if (-not $Mode)    { $Mode    = 'perf' }
if (-not $Project) { $Project = 'C:\Users\dcl\unity-explorer\Explorer' }
if (-not $Unity)   { $Unity   = 'C:\Users\dcl\UnityEditors\6000.4.11f1\Editor\Unity.exe' }
if (-not $User)    { $User    = 'dcl' }
$ErrorActionPreference = 'SilentlyContinue'

# mode -> (Scheduled-Task name, harness entry point, CSV the run writes)
$map = @{
  perf   = @{ Task = 'DclUnityPerf';   Method = 'RunPerfHeadless';         Csv = 'harness-perf.csv' }
  cpu    = @{ Task = 'DclUnityCpu';    Method = 'RunCpuBreakdownHeadless'; Csv = 'harness-cpu.csv' }
  render = @{ Task = 'DclUnityRender'; Method = 'RunRenderDecompHeadless'; Csv = 'harness-render.csv' }
  shadow = @{ Task = 'DclUnityShadow'; Method = 'RunShadowPerfHeadless';   Csv = 'harness-shadow.csv' }
}
$c = $map[$Mode]
if (-not $c) { Write-Output "BAD_MODE:$Mode"; exit 1 }
$Method = 'DCL.Harness.DclPlaytestHarness.' + $c.Method
$Log    = "C:\Users\$User\harness-$Mode-run.log"
$Csv    = "C:\Users\$User\$($c.Csv)"

# 1. Kill everything Unity-related and let handles release.
Get-Process -Name 'Unity','Unity Hub','Unity.Licensing.Client','UnityShaderCompiler',
  'bee_backend','UnityCrashHandler64','Decentraland' -EA SilentlyContinue | Stop-Process -Force
Start-Sleep 8

# 2. Nuke caches that break a cold boot (domain-reload hangs / phantom CS errors).
foreach ($d in 'Library\ScriptAssemblies','Library\Bee','Library\StateCache','Temp') {
  Remove-Item (Join-Path $Project $d) -Recurse -Force -EA SilentlyContinue
}

# 3. Warm the licensing client before the editor.
Start-Process 'C:\Program Files\Unity Hub\Unity Hub.exe' -WindowStyle Minimized
Start-Sleep 15

# 4. Clear prior outputs so polling can't read a stale CSV/log.
Remove-Item $Log,$Csv -Force -EA SilentlyContinue

# 5. Launch in the interactive desktop session via a Scheduled Task.
$arg  = "-projectPath `"$Project`" -executeMethod $Method -logFile `"$Log`""
$act  = New-ScheduledTaskAction -Execute $Unity -Argument $arg
$prin = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive
Register-ScheduledTask -TaskName $c.Task -Action $act -Principal $prin -Force | Out-Null
Start-ScheduledTask -TaskName $c.Task
Write-Output "TELEMETRY_LAUNCHED:$Mode"

# launch-atlas-warm.ps1 — launch the full atlas capture (RunAtlasHeadless) WITHOUT nuking
# ScriptAssemblies, so it reuses the warm assemblies the batch compile just built (avoids the
# cold-recompile-during-GUI-startup domain-reload hang). NO-NOISE: RunAtlasHeadless teleports
# to the quiet parcel and never sends chat. Clears prior outputs + shots first.
$ErrorActionPreference='SilentlyContinue'
$proj='C:\Users\dcl\unity-explorer\Explorer'
$unity='C:\Users\dcl\UnityEditors\6000.4.11f1\Editor\Unity.exe'
Get-Process -Name 'Unity','Unity Hub','Unity.Licensing.Client','UnityShaderCompiler','bee_backend','UnityCrashHandler64','Decentraland' -EA SilentlyContinue | Stop-Process -Force
Start-Sleep 6
# keep Library/ScriptAssemblies + Bee warm (built by reset-and-batchcompile) — do NOT nuke
Remove-Item 'C:\Users\dcl\harness-run.log','C:\Users\dcl\harness-report.json' -Force -EA SilentlyContinue
Remove-Item 'C:\Users\dcl\harness-shots\*.png' -Force -EA SilentlyContinue
Start-Process 'C:\Program Files\Unity Hub\Unity Hub.exe' -WindowStyle Minimized
Start-Sleep 15
$arg = '-projectPath "'+$proj+'" -executeMethod DCL.Harness.DclPlaytestHarness.RunAtlasHeadless -logFile "C:\Users\dcl\harness-run.log"'
$act = New-ScheduledTaskAction -Execute $unity -Argument $arg
$prin = New-ScheduledTaskPrincipal -UserId 'dcl' -LogonType Interactive
Register-ScheduledTask -TaskName 'DclUnityHarness' -Action $act -Principal $prin -Force | Out-Null
Start-ScheduledTask -TaskName 'DclUnityHarness'
Write-Output 'WARM_ATLAS_LAUNCHED'

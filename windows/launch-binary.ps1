# =============================================================================
# launch-binary.ps1 — launch the built Windows player in autopilot and collect
# telemetry. Target: "windows binary" (run half).
#
# Autopilot mode self-drives: logs in, waits for LoadingStatus.Completed, stands
# at spawn sampling CPU/GPU frame time, writes CSV + summary, then quits — no GUI
# interaction needed. Like the editor harness, it's launched via an Interactive
# Scheduled Task so it gets a real graphics device (session 0 over SSH does not).
#
#   .\windows\launch-binary.ps1 -Realm http://localhost:8000 -Position "0,0"
# =============================================================================
param(
  [string]$Exe      = 'C:\Users\dcl\AppData\Local\DecentralandLauncherLight\latest\Decentraland.exe',
  [string]$Realm,
  [string]$Position = '0,0',
  [string]$Csv      = 'C:\Users\dcl\player-perf.csv',
  [string]$Summary  = 'C:\Users\dcl\player-summary.txt',
  [string]$User     = 'dcl',
  [string]$TaskName = 'DclPlayerAutopilot',
  [string]$ExtraArgs = ''
)
$ErrorActionPreference = 'SilentlyContinue'

Get-Process -Name 'Decentraland','UnityCrashHandler64' -EA SilentlyContinue | Stop-Process -Force
Start-Sleep 3
Remove-Item $Csv,$Summary -Force -EA SilentlyContinue

$argParts = @(
  '--autopilot',
  '--csv',"`"$Csv`"",
  '--summary',"`"$Summary`"",
  '--skip-version-check',
  '--position',$Position
)
if ($Realm) { $argParts += @('--realm',$Realm) }
if ($ExtraArgs) { $argParts += $ExtraArgs }
$arg = $argParts -join ' '

$act  = New-ScheduledTaskAction -Execute $Exe -Argument $arg
$prin = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive
Register-ScheduledTask -TaskName $TaskName -Action $act -Principal $prin -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
Write-Output 'PLAYER_LAUNCHED'

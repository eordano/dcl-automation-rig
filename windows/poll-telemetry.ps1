# =============================================================================
# poll-telemetry.ps1 — one status line for a running telemetry mode. Target:
# "windows editor". Driven by vm/run-telemetry.sh (pipes this in with `$Mode`
# pre-assigned). Prints:  unity=<proc count> log=<bytes> csv=<True|False>
#   * csv=True            -> the run finished and wrote its CSV (done)
#   * log frozen, small   -> still licensing/compiling (stall, retry)
# =============================================================================
if (-not $Mode) { $Mode = 'perf' }
if (-not $User) { $User = 'dcl' }
$csvmap = @{ perf = 'harness-perf.csv'; cpu = 'harness-cpu.csv'; render = 'harness-render.csv'; shadow = 'harness-shadow.csv' }
$log = "C:\Users\$User\harness-$Mode-run.log"
$csv = "C:\Users\$User\$($csvmap[$Mode])"

$u = (Get-Process Unity -EA SilentlyContinue | Measure-Object).Count
$l = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }
$c = Test-Path $csv
Write-Output "unity=$u log=$l csv=$c"

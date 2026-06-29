$u = (Get-Process Unity -EA SilentlyContinue | Measure-Object).Count
$l = 0
if (Test-Path 'C:\Users\dcl\batchcompile.log') { $l = (Get-Item 'C:\Users\dcl\batchcompile.log').Length }
$t = (Get-ScheduledTask -TaskName 'DclBatchCompile' -EA SilentlyContinue).State
Write-Output "unity=$u log=$l task=$t"

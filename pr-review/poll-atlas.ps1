$l='C:\Users\dcl\harness-run.log'
$u = (Get-Process Unity -EA SilentlyContinue | Measure-Object).Count
$sz = 0
if (Test-Path $l) { $sz = (Get-Item $l).Length }
$j = Test-Path 'C:\Users\dcl\harness-report.json'
Write-Output "unity=$u log=$sz json=$j"

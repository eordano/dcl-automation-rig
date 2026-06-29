$ErrorActionPreference='SilentlyContinue'
$proj='C:\Users\dcl\unity-explorer\Explorer'
$unity='C:\Users\dcl\UnityEditors\6000.4.11f1\Editor\Unity.exe'
Get-Process -Name 'Unity','Unity Hub','Unity.Licensing.Client','UnityShaderCompiler','bee_backend','UnityCrashHandler64' -EA SilentlyContinue | Stop-Process -Force
Start-Sleep 6
Remove-Item ($proj+'\Library\ScriptAssemblies') -Recurse -Force -EA SilentlyContinue
Remove-Item ($proj+'\Library\Bee') -Recurse -Force -EA SilentlyContinue
Remove-Item 'C:\Users\dcl\batchcompile.log' -Force -EA SilentlyContinue
# batchmode -quit: compiles scripts then exits; no GUI Safe-Mode modal, logs 'error CS' on failure.
$arg = '-batchmode -quit -projectPath "'+$proj+'" -logFile "C:\Users\dcl\batchcompile.log"'
$act = New-ScheduledTaskAction -Execute $unity -Argument $arg
$prin = New-ScheduledTaskPrincipal -UserId 'dcl' -LogonType Interactive
Register-ScheduledTask -TaskName 'DclBatchCompile' -Action $act -Principal $prin -Force | Out-Null
Start-ScheduledTask -TaskName 'DclBatchCompile'
Write-Output 'BATCHCOMPILE_LAUNCHED'

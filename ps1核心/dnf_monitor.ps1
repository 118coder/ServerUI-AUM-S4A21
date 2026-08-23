$pidFile = "$env:TEMP\dnf_bat.pid"
if (-not (Test-Path $pidFile)) { exit }
$batPid = try { [int](Get-Content $pidFile -Raw) } catch { exit }
while (Get-Process -Id $batPid -ErrorAction SilentlyContinue) { Start-Sleep 3 }
Start-Sleep 2
$p = Get-WmiObject Win32_Process -Filter "Name='cmd.exe' and CommandLine like '%start-server.bat%'"
if ($p) { cmd /c "taskkill /F /T /PID $($p.ProcessId) >nul 2>&1" | Out-Null }
cmd /c "taskkill /f /im DfoServer.exe >nul 2>&1" | Out-Null
netstat -ano | findstr ":7001 " | ForEach-Object {
    $t = $_ -split '\s+'
    $targetPid = $t[$t.Count-1]
    cmd /c "taskkill /f /pid $targetPid >nul 2>&1" | Out-Null
}

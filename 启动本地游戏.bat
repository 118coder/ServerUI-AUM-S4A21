@echo off
chcp 936 >nul
setlocal EnableDelayedExpansion

if "%1"=="_go" goto main
start /min "" cmd.exe /c "%~f0" _go
exit /b

:main
title DNF 单机游戏 - 启动脚本
set "BASE=%~dp0"

rem 保存当前 cmd.exe 的 PID，后台监控用
powershell -NoProfile -ExecutionPolicy Bypass -File "%BASE%ps1核心\get_pid.ps1"

rem 后台监控: 窗口关闭时关闭服务端
start /b "" powershell -NoProfile -ExecutionPolicy Bypass -File "%BASE%ps1核心\dnf_monitor.ps1"

echo ========================================
echo   DNF 单机游戏 - 一键启动脚本
echo ========================================
echo.

echo [查找] start-server.bat...
set "SRV_BAT="
for /f "delims=" %%i in ('dir /s /b "%BASE%ServerS4A12-AUM\start-server.bat" 2^>nul') do set "SRV_BAT=%%i"
if not defined SRV_BAT (
    echo [错误] 未找到 start-server.bat
    echo   请确认 ServerS4A12-AUM 目录下存在 start-server.bat
    echo   如果不存在，请在 ServerUI 中执行一次更新。
    pause
    exit /b
)
echo   OK: !SRV_BAT!

echo.
echo [查找] DNF.exe...
set "CLI_EXE="
for /f "delims=" %%i in ('dir /s /b "%BASE%DNF.exe" 2^>nul') do set "CLI_EXE=%%i"
if defined CLI_EXE goto found_cli
for /f "delims=" %%i in ('dir /s /b "%BASE%..\DNF.exe" 2^>nul') do set "CLI_EXE=%%i"
if defined CLI_EXE goto found_cli
for /f "delims=" %%i in ('dir /s /b "%BASE%..\..\DNF.exe" 2^>nul') do set "CLI_EXE=%%i"
if defined CLI_EXE goto found_cli
echo [错误] 未找到 DNF.exe
echo   请在脚本所在目录或上级目录放置 DNF.exe 游戏客户端。
pause
exit /b

:found_cli
echo   OK: !CLI_EXE!
for %%i in ("!CLI_EXE!") do set "CLI_DIR=%%~dpi"

set "CLI_BAT="
if exist "!CLI_DIR!本地游戏S4.bat" (
    set "CLI_BAT=!CLI_DIR!本地游戏S4.bat"
) else if exist "!CLI_DIR!单机游戏启动.bat" (
    set "CLI_BAT=!CLI_DIR!单机游戏启动.bat"
)
if defined CLI_BAT (
    echo   客户端脚本: !CLI_BAT!
)

echo.
echo [1/2] 启动服务端...
> "%temp%\dnf_srv.vbs" echo Set ws=CreateObject("WScript.Shell"^):ws.Run """!SRV_BAT!""",0,False
cscript //nologo "%temp%\dnf_srv.vbs"
del "%temp%\dnf_srv.vbs"

echo   等待服务端就绪 (端口 7001)...
set /a COUNT=0
:wait_srv
timeout /t 2 /nobreak >nul
netstat -an | findstr ":7001 .*LISTENING" >nul 2>&1
if not errorlevel 1 goto srv_ready
set /a COUNT+=2
if !COUNT! lss 30 goto wait_srv
echo [错误] 等待超时 (30秒)，端口 7001 未就绪
:srv_ready
echo   服务端已就绪 (等待 !COUNT! 秒)
timeout /t 3 /nobreak >nul

echo.
echo [2/2] 启动游戏客户端...
if defined CLI_BAT (
    > "%temp%\dnf_cli.vbs" echo Set ws=CreateObject("WScript.Shell"^):ws.Run "cmd.exe /c cd /d ""!CLI_DIR!"" && ""!CLI_BAT!""",0,False
    cscript //nologo "%temp%\dnf_cli.vbs"
    del "%temp%\dnf_cli.vbs"
) else (
    > "%temp%\dnf_cli.vbs" echo Set ws=CreateObject("WScript.Shell"^):ws.Run """!CLI_EXE!"" 99?127.0.0.1?7001?10038?de509f65e9ccaae621cb7278fc2b8e6c?01?1?0?0?0?0?1?9n2b1c8r3w7y?0?0?19847",0,False
    cscript //nologo "%temp%\dnf_cli.vbs"
    del "%temp%\dnf_cli.vbs"
)

echo.
echo ========================================
echo   游戏已启动
echo ========================================
echo.

echo   dnf.exe游戏退出后，本窗口将会自动关闭服务端，但是会有延迟，请静候bat指令反应
:wait_exit
timeout /t 3 /nobreak >nul
tasklist /fi "imagename eq DNF.exe" 2>nul | findstr /i "DNF.exe" >nul 2>&1
if not errorlevel 1 goto wait_exit

echo.
echo 游戏已退出，关闭服务端...
for /f "tokens=2 delims== " %%a in ('wmic process where "name='cmd.exe' and commandline like '%%start-server.bat%%'" get processid /value 2^>nul') do (
    taskkill /F /T /PID %%a >nul 2>&1
)
taskkill /f /im DfoServer.exe >nul 2>&1
for /f "tokens=5" %%a in ('netstat -ano ^| findstr ":7001 " 2^>nul') do (
    >nul 2>&1 taskkill /f /pid %%a
)
echo 服务端已关闭
timeout /t 2 /nobreak >nul
exit

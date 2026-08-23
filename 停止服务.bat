@echo off
chcp 936 >nul
setlocal EnableDelayedExpansion

rem ============================================================
rem  停止服务.bat — 终止 DfoServer 及其关联的所有程序进程
rem ============================================================
rem
rem  功能: 强制结束占用端口 7001 的进程 + 清理所有残留 DfoServer.exe
rem  适用: 启动本地游戏.bat 未正常退出时，手动清理残留进程
rem  原理: 通过 netstat 查找监听 7001 端口的进程 PID，然后 taskkill
rem
rem  端口说明:
rem    7001 = DfoServer 游戏频道端口 (TCP)
rem    10011 = DfoServer 游戏端口 (TCP)
rem    DfoServer.exe = 服务端主进程
rem
rem  修改建议:
rem    如果服务端使用了不同端口，修改 findstr 后面的端口号即可

echo ========================================
echo   停止服务 — 清理 DfoServer 相关进程
echo ========================================
echo.

rem ----- 1. 通过端口 7001 查找并终止对应进程 -----
echo [1/4] 正在查找占用端口 7001 的进程...
set "FOUND="
for /f "tokens=5" %%a in ('netstat -ano ^| findstr ":7001 .*LISTENING" 2^>nul') do (
    set "PID=%%a"
    set "FOUND=1"
    echo   发现进程 PID: !PID!，正在终止...
    taskkill /f /pid !PID! >nul 2>&1
    timeout /t 1 /nobreak >nul
)
if not defined FOUND echo   端口 7001 未被占用，跳过。

rem ----- 2. 终止所有 DfoServer.exe 进程 -----
echo.
echo [2/4] 正在终止所有 DfoServer.exe 进程...
taskkill /f /im DfoServer.exe >nul 2>&1
if errorlevel 1 (echo   DfoServer.exe 未运行，跳过) else (echo   已终止 DfoServer.exe)

rem ----- 3. 终止所有 cmd.exe 运行 start-server.bat 的窗口 -----
echo.
echo [3/4] 正在关闭服务端脚本窗口...
for /f "tokens=2" %%a in ('tasklist /fi "imagename eq cmd.exe" /fo table 2^>nul ^| findstr /i "cmd.exe"') do (
    rem 跳过标题行
)
rem 直接通过窗口标题关闭
taskkill /f /fi "WINDOWTITLE eq start-server*" >nul 2>&1

rem ----- 4. 确认清理结果 -----
echo.
echo [4/4] 验证清理结果...
netstat -an | findstr ":7001 " >nul 2>&1
if errorlevel 1 (
    echo   端口 7001 已释放。
) else (
    echo   警告: 端口 7001 仍被占用，请手动检查。
)
tasklist /fi "imagename eq DfoServer.exe" 2>nul | findstr /i "DfoServer.exe" >nul 2>&1
if errorlevel 1 (
    echo   DfoServer.exe 已全部终止。
) else (
    echo   警告: 仍有 DfoServer.exe 进程运行中。
)

echo.
echo ========================================
echo   清理完成。可以重新启动服务端。
echo ========================================
echo.
pause

@echo off
REM ============================================================
REM  GM Tool launcher (pure ASCII wrapper).
REM  All Chinese text / logic lives in gmtool.ps1 (UTF-8 BOM),
REM  so this .bat stays ASCII-only and is immune to codepage issues.
REM  gmtool.ps1 mirrors ServerUI's GM-tool launch (workdir = publish).
REM ============================================================
cd /d "%~dp0"
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ps1核心\gmtool.ps1"
if errorlevel 1 (
    echo.
    echo [launcher] gmtool.ps1 exited with an error.
    pause
)

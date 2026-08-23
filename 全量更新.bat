chcp 65001
@echo off
cls
echo ========================================
echo   ServerS4A12 - 全量更新
echo ========================================
echo.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ps1核心\update.ps1" -FullSync
pause
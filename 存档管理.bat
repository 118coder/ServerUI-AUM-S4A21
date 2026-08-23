chcp 65001
@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ps1核心\save-switch.ps1"
pause
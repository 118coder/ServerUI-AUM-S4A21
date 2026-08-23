@echo off
title ServerS4A12-LocalBuild
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0ps1核心\进行本地编译.ps1"
pause

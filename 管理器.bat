chcp 65001
@echo off
cd /d "%~dp0"
if exist "ServerUI-兼容模式.exe" (start "" "ServerUI-兼容模式.exe") else if exist "ServerUI-无依赖版.exe" (start "" "ServerUI-无依赖版.exe") else if exist "ServerUI-有依赖版.exe" (start "" "ServerUI-有依赖版.exe") else (echo ServerUI not found.&pause)

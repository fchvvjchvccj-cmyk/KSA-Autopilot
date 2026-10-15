@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0自动驾驶Mod\install.ps1" -Action Uninstall
if errorlevel 1 pause

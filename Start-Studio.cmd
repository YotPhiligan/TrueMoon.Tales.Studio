@echo off
setlocal
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Studio.ps1" %*
if errorlevel 1 pause

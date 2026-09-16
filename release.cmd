@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\publish-release.ps1" %*
pause

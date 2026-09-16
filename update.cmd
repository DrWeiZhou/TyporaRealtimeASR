@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\update.ps1" %*
pause

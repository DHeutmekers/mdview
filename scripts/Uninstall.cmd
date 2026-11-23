@echo off
copy /y "%~dp0uninstall.ps1" "%TEMP%\mdview-uninstall.ps1" >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%TEMP%\mdview-uninstall.ps1"
pause

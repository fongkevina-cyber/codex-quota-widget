@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Share.ps1"
if errorlevel 1 (
  echo.
  echo Installation failed. Please read the error above.
  pause
)

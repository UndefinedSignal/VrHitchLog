@echo off
rem Same as run.cmd, plus WPR traces of big hitches. Asks for administrator rights.
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
cd /d "%~dp0"
VrHitchLog.exe --wpr %*
pause

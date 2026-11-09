@echo off
rem Builds VrHitchLog into the "out" folder next to this file. Needs the .NET SDK 8 or newer (comes with Visual Studio).
cd /d "%~dp0"
dotnet build VrHitchLog.csproj -c Release -o out
if errorlevel 1 (echo. & echo Build failed. & pause & exit /b 1)
echo.
echo Done: out\VrHitchLog.exe  (run out\run.cmd, or out\run-wpr.cmd for WPR traces)
pause

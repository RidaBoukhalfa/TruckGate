@echo off
rem TruckGate - create the first administrator (run once after installation).
net session >nul 2>&1
if errorlevel 1 (
  powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath '%~f0'"
  exit /b
)
cd /d "%~dp0..\server"
echo TruckGate - first administrator account
echo.
TruckGate.Api.exe create-admin
echo.
pause

@echo off
setlocal
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting Administrator privileges...
    powershell -Command "Start-Process '%~dpnx0' -Verb RunAs"
    exit /b
)

echo ================================================================
echo   Win2Linux EFI System Partition (ESP) Deployer
echo ================================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deploy-EfiPartition.ps1"
echo.
pause

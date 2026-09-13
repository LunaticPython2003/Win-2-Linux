@echo off
setlocal
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting Administrator privileges...
    powershell -Command "Start-Process '%~dpnx0' -Verb RunAs"
    exit /b
)

echo ================================================================
echo   Win2Linux EFI Bootloader SBAT (grub,5) Secure Boot Updater
echo ================================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Update-Bootloader.ps1"
echo.
pause

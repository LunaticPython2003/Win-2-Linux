@echo off
setlocal
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting Administrator privileges...
    powershell -Command "Start-Process '%~dpnx0' -Verb RunAs"
    exit /b
)

set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
set "PATH=%USERPROFILE%\.dotnet;%PATH%"
echo ================================================================
echo   Win2Linux Dual-Boot Installer (WinUI 3 Fluent UI - Elevated)
echo ================================================================
echo Starting WinUI 3 desktop application...
dotnet run --project "%~dp0src\Win2Linux.UI"

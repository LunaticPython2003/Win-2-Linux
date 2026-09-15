# Ensure script runs with Administrator privileges required for storage operations
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Elevating with Administrator privileges..." -ForegroundColor Yellow
    Start-Process powershell -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -NoProfile -File `"$PSCommandPath`""
    exit
}

$env:DOTNET_ROOT = "$env:USERPROFILE\.dotnet"
$env:Path = "$env:USERPROFILE\.dotnet;" + $env:Path
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "  Win2Linux Dual-Boot Installer (WinUI 3 Fluent UI - Elevated)  " -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "Starting WinUI 3 desktop application..." -ForegroundColor Green
dotnet run --project "$PSScriptRoot\src\Win2Linux.UI"

<#
.SYNOPSIS
Launches the Win-2-Linux UI application in development mode.
.DESCRIPTION
This script automatically checks for Administrator privileges. If it is not running
as Administrator, it will seamlessly prompt for UAC elevation and relaunch itself.
Once elevated, it uses the .NET SDK to compile and run the Win2Linux.UI project.
#>

$ErrorActionPreference = "Stop"

# 1. Self-Elevate to Administrator if necessary
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Administrator privileges are required to manage partitions and bootloaders." -ForegroundColor Yellow
    Write-Host "Requesting elevation..." -ForegroundColor Cyan
    
    # Relaunch powershell elevated and run this script
    Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs
    exit
}

# 2. Navigate to the script's directory to ensure paths resolve correctly
$projectRoot = Split-Path -Parent $PSCommandPath
Set-Location -Path $projectRoot

# 3. Check for .NET SDK
if (-not (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    Write-Host ""
    Write-Host "ERROR: The 'dotnet' command was not found." -ForegroundColor Red
    Write-Host "You must install the .NET 8.0 SDK (or newer) to run this project from source." -ForegroundColor Yellow
    Write-Host "Download: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Cyan
    Write-Host ""
    Read-Host "Press Enter to exit"
    exit 1
}

# 4. Launch the application
Write-Host "Starting Win-2-Linux UI in Development Mode..." -ForegroundColor Green
dotnet run --project "src\Win2Linux.UI\Win2Linux.UI.csproj"

# Keep the window open if the app crashes so the developer can see the error
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Application exited with error code $LASTEXITCODE." -ForegroundColor Red
    Read-Host "Press Enter to exit"
}

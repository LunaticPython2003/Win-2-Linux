# Ensure Administrator privileges
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Requesting Administrator privileges..." -ForegroundColor Yellow
    Start-Process powershell -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -NoProfile -File `"$PSCommandPath`""
    exit
}

Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "  Removing ESP Partition (Disk 1, Partition 3) via Diskpart...  " -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan

$script = @"
select disk 1
select partition 3
delete partition override
select partition 2
extend
exit
"@

$tempScript = "$env:TEMP\win2linux_remove_esp.txt"
Set-Content -Path $tempScript -Value $script -Encoding Ascii

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "diskpart.exe"
$psi.Arguments = "/s `"$tempScript`""
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $false
$proc = [System.Diagnostics.Process]::Start($psi)
$proc.WaitForExit()

Remove-Item $tempScript -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Partition 3 removed and D: extended successfully!" -ForegroundColor Green
Start-Sleep -Seconds 3

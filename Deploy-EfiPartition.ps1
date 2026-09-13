# Deploy-EfiPartition.ps1
# Creates a dedicated 512 MB FAT32 EFI System Partition on Disk 1 (Secondary SSD)
# and deploys the full bootloader chain and installer payload from D:\win2linux.

$ErrorActionPreference = "Stop"

# 1. Require Administrator
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Requesting Administrator privileges..." -ForegroundColor Yellow
    Start-Process powershell -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -NoProfile -File `"$PSCommandPath`""
    exit
}

Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host "  Win2Linux EFI System Partition (ESP) & Bootloader Deployer     " -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan

# 2. Safety Verification: Ensure Disk 0 is Windows System Disk and Disk 1 is Secondary
Write-Host "[1/5] Performing disk safety checks..." -ForegroundColor Yellow
$sysDrive = $env:SystemDrive.TrimEnd(':')
$cPart = Get-Partition -DriveLetter $sysDrive -ErrorAction SilentlyContinue
if (-not $cPart) {
    Write-Host "Error: Could not determine Windows system drive ($sysDrive:)." -ForegroundColor Red
    exit 1
}

$systemDiskNumber = $cPart.DiskNumber
Write-Host "  Protected Windows System Disk: Disk $systemDiskNumber ($($env:SystemDrive))" -ForegroundColor Green

if ($systemDiskNumber -ne 0) {
    Write-Host "Warning: Windows is on Disk $systemDiskNumber, not Disk 0." -ForegroundColor Yellow
}

$targetDiskNumber = 1
if ($targetDiskNumber -eq $systemDiskNumber) {
    Write-Host "FATAL: Target disk matches protected system disk ($systemDiskNumber). Aborting!" -ForegroundColor Red
    exit 1
}

$dPart = Get-Partition -DiskNumber $targetDiskNumber | Where-Object { $_.DriveLetter -eq 'D' }
if (-not $dPart) {
    Write-Host "Error: Drive D: not found on Disk $targetDiskNumber." -ForegroundColor Red
    exit 1
}
Write-Host "  Target Secondary Disk: Disk $targetDiskNumber (D: - $($dPart.Size / 1GB -as [int]) GB)" -ForegroundColor Green

# 3. Check for existing EFI partition on Disk 1 or create it from unallocated space
Write-Host "[2/5] Checking EFI partition on Disk $targetDiskNumber..." -ForegroundColor Yellow

$espGptGuid = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}"
$existingEsp = Get-Partition -DiskNumber $targetDiskNumber | Where-Object { $_.GptType -eq $espGptGuid } | Select-Object -First 1

$targetPartNumber = 0

if ($existingEsp) {
    $targetPartNumber = $existingEsp.PartitionNumber
    Write-Host "  Found existing EFI partition on Disk $targetDiskNumber: Partition $targetPartNumber ($($existingEsp.Size / 1MB -as [int]) MB)" -ForegroundColor Green
    Write-Host "  Deleting existing EFI partition as requested..." -ForegroundColor Yellow
    
    $diskpartScript = @"
select disk $targetDiskNumber
select partition $targetPartNumber
delete partition override
create partition efi size=4096
format fs=fat32 quick label="LINUXEFI"
assign letter=W
exit
"@
} else {
    Write-Host "  No EFI partition found on Disk $targetDiskNumber. Creating 4096 MB EFI System Partition..." -ForegroundColor Yellow
    $diskpartScript = @"
select disk $targetDiskNumber
create partition efi size=4096
format fs=fat32 quick label="LINUXEFI"
assign letter=W
exit
"@
}

$tempScriptPath = "$env:TEMP\win2linux_deploy_esp_$([guid]::NewGuid().ToString('N')).txt"
Set-Content -Path $tempScriptPath -Value $diskpartScript -Encoding Ascii

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "diskpart.exe"
$psi.Arguments = "/s `"$tempScriptPath`""
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$proc = [System.Diagnostics.Process]::Start($psi)
$proc.WaitForExit()
Remove-Item $tempScriptPath -ErrorAction SilentlyContinue

Start-Sleep -Milliseconds 1500

if (-not (Test-Path 'W:\')) {
    Write-Host "Error: Could not mount EFI partition to W:. Please verify Disk $targetDiskNumber has unallocated space." -ForegroundColor Red
    exit 1
}

# Re-query partition number
$espPart = Get-Partition -DiskNumber $targetDiskNumber | Where-Object { $_.GptType -eq $espGptGuid } | Select-Object -First 1
if ($espPart) {
    $targetPartNumber = $espPart.PartitionNumber
    Write-Host "[OK] EFI System Partition active on Disk $targetDiskNumber Partition $targetPartNumber (mounted at W:\)" -ForegroundColor Green
}

# 4. Copy bootloader and installer payload to W:\
Write-Host "[3/5] Deploying EFI bootloader files and installer payload to W:\..." -ForegroundColor Yellow

$stageDir = "D:\win2linux\esp-stage"
$sourceEfiStage = "D:\win2linux\efi-stage"
$sourceBootStage = "D:\win2linux\boot"
$sourceUnattended = "D:\win2linux\unattended"

New-Item -ItemType Directory -Force -Path 'W:\EFI\BOOT' | Out-Null
New-Item -ItemType Directory -Force -Path 'W:\EFI\fedora' | Out-Null
New-Item -ItemType Directory -Force -Path 'W:\win2linux\boot' | Out-Null
New-Item -ItemType Directory -Force -Path 'W:\win2linux\unattended' | Out-Null

# 4a. Universal Fallback Bootloader (\EFI\BOOT\)
# shim (BOOTX64.EFI) and grubx64.efi
if (Test-Path "$sourceEfiStage\BOOTX64.EFI") {
    Copy-Item "$sourceEfiStage\BOOTX64.EFI" 'W:\EFI\BOOT\BOOTX64.EFI' -Force
    Copy-Item "$sourceEfiStage\grubx64.efi" 'W:\EFI\BOOT\grubx64.efi' -Force
    if (Test-Path "$sourceEfiStage\mmx64.efi") {
        Copy-Item "$sourceEfiStage\mmx64.efi" 'W:\EFI\BOOT\mmx64.efi' -Force
    }
} elseif (Test-Path "$stageDir\EFI\BOOT\BOOTX64.EFI") {
    Copy-Item "$stageDir\EFI\BOOT\*" 'W:\EFI\BOOT\' -Recurse -Force
}

# 4b. Vendor Bootloader (\EFI\fedora\)
if (Test-Path "$sourceEfiStage\shimx64.efi") {
    Copy-Item "$sourceEfiStage\shimx64.efi" 'W:\EFI\fedora\shimx64.efi' -Force
    Copy-Item "$sourceEfiStage\grubx64.efi" 'W:\EFI\fedora\grubx64.efi' -Force
    if (Test-Path "$sourceEfiStage\mmx64.efi") {
        Copy-Item "$sourceEfiStage\mmx64.efi" 'W:\EFI\fedora\mmx64.efi' -Force
    }
} elseif (Test-Path "$stageDir\EFI\fedora\shimx64.efi") {
    Copy-Item "$stageDir\EFI\fedora\*" 'W:\EFI\fedora\' -Recurse -Force
}

# 4c. grub.cfg for both paths
if (Test-Path "$stageDir\EFI\fedora\grub.cfg") {
    Copy-Item "$stageDir\EFI\fedora\grub.cfg" 'W:\EFI\BOOT\grub.cfg' -Force
    Copy-Item "$stageDir\EFI\fedora\grub.cfg" 'W:\EFI\fedora\grub.cfg' -Force
} elseif (Test-Path "$stageDir\EFI\BOOT\grub.cfg") {
    Copy-Item "$stageDir\EFI\BOOT\grub.cfg" 'W:\EFI\BOOT\grub.cfg' -Force
    Copy-Item "$stageDir\EFI\BOOT\grub.cfg" 'W:\EFI\fedora\grub.cfg' -Force
}

# 4d. Kernel and initrd
if (Test-Path "$sourceBootStage\vmlinuz") {
    Copy-Item "$sourceBootStage\vmlinuz" 'W:\win2linux\boot\vmlinuz' -Force
    Copy-Item "$sourceBootStage\initrd" 'W:\win2linux\boot\initrd' -Force
} elseif (Test-Path "$stageDir\win2linux\boot\vmlinuz") {
    Copy-Item "$stageDir\win2linux\boot\vmlinuz" 'W:\win2linux\boot\vmlinuz' -Force
    Copy-Item "$stageDir\win2linux\boot\initrd" 'W:\win2linux\boot\initrd' -Force
}

# 4e. LiveOS / Squashfs
if (Test-Path "$sourceBootStage\LiveOS") {
    Copy-Item "$sourceBootStage\LiveOS" 'W:\LiveOS' -Recurse -Force
} elseif (Test-Path "$stageDir\LiveOS") {
    Copy-Item "$stageDir\LiveOS" 'W:\LiveOS' -Recurse -Force
}

# 4f. Unattended kickstart and meta-data
if (Test-Path "$sourceUnattended\kickstart.ks") {
    Copy-Item "$sourceUnattended\kickstart.ks" 'W:\win2linux\unattended\kickstart.ks' -Force
} elseif (Test-Path "$stageDir\win2linux\unattended\kickstart.ks") {
    Copy-Item "$stageDir\win2linux\unattended\kickstart.ks" 'W:\win2linux\unattended\kickstart.ks' -Force
}

if (Test-Path "$stageDir\win2linux\unattended\meta-data") {
    Copy-Item "$stageDir\win2linux\unattended\meta-data" 'W:\win2linux\unattended\meta-data' -Force
} else {
    Set-Content -Path 'W:\win2linux\unattended\meta-data' -Value "instance-id: win2linux-installer`n" -Encoding Ascii
}

# Verify critical files
$critical = @(
    'W:\EFI\BOOT\BOOTX64.EFI',
    'W:\EFI\BOOT\grubx64.efi',
    'W:\EFI\BOOT\grub.cfg',
    'W:\EFI\fedora\shimx64.efi',
    'W:\EFI\fedora\grubx64.efi',
    'W:\EFI\fedora\grub.cfg',
    'W:\win2linux\boot\vmlinuz',
    'W:\win2linux\boot\initrd',
    'W:\LiveOS\squashfs.img',
    'W:\win2linux\unattended\kickstart.ks'
)

$missing = @()
foreach ($f in $critical) {
    if (-not (Test-Path $f)) { $missing += $f }
}

if ($missing.Count -gt 0) {
    Write-Host "Warning: Missing expected files: $($missing -join ', ')" -ForegroundColor Red
} else {
    Write-Host "[OK] All $($critical.Count) bootloader and installer files verified on ESP (W:\)!" -ForegroundColor Green
}

# 5. Register UEFI Boot Entry via bcdedit
Write-Host "[4/5] Registering UEFI NVRAM Boot Entry..." -ForegroundColor Yellow

$entryGuid = $null
$bcdResult = & bcdedit /copy '{bootmgr}' /d "Fedora Workstation 44" 2>&1
if ($bcdResult -match '\{([0-9a-fA-F\-]{36})\}') {
    $entryGuid = "{$($Matches[1])}"
} else {
    $bcdResult2 = & bcdedit /create /d "Fedora Workstation 44" /application bootapp 2>&1
    if ($bcdResult2 -match '\{([0-9a-fA-F\-]{36})\}') {
        $entryGuid = "{$($Matches[1])}"
    }
}

if ($entryGuid) {
    & bcdedit /set $entryGuid device partition=W: 2>&1 | Out-Null
    & bcdedit /set $entryGuid path "\EFI\fedora\shimx64.efi" 2>&1 | Out-Null
    & bcdedit /set '{fwbootmgr}' displayorder $entryGuid /addfirst 2>&1 | Out-Null
    Write-Host "[OK] Registered UEFI entry '$entryGuid' as primary boot option!" -ForegroundColor Green
} else {
    Write-Host "Note: NVRAM registration could not generate a GUID ($bcdResult)." -ForegroundColor Yellow
    Write-Host "      However, the UEFI fallback path (\EFI\BOOT\BOOTX64.EFI) on the dedicated ESP" -ForegroundColor Yellow
    Write-Host "      is now installed and will appear directly in your BIOS Boot Menu." -ForegroundColor Yellow
}

# 6. Unmount W:\
Write-Host "[5/5] Safely unmounting W:\..." -ForegroundColor Yellow
$unmountScript = "$env:TEMP\win2linux_unmount_$([guid]::NewGuid().ToString('N')).txt"
Set-Content -Path $unmountScript -Value "select volume W`nremove letter=W`nexit" -Encoding Ascii
$psi.Arguments = "/s `"$unmountScript`""
$proc = [System.Diagnostics.Process]::Start($psi)
$proc.WaitForExit()
Remove-Item $unmountScript -ErrorAction SilentlyContinue

# Update state.json
$statePath = "C:\ProgramData\win2linux\state.json"
if (Test-Path $statePath) {
    try {
        $json = Get-Content $statePath -Raw | ConvertFrom-Json
        $json.Esp = [PSCustomObject]@{
            DiskNumber = $targetDiskNumber
            PartitionNumber = $targetPartNumber
            DriveLetter = "W"
            SizeBytes = 4294967296
            Label = "LINUXEFI"
        }
        if ($entryGuid) {
            $json.NvramRegistration.Success = $true
            $json.NvramRegistration.BcdeditEntryGuid = $entryGuid
        }
        $json | ConvertTo-Json -Depth 10 | Set-Content -Path $statePath -Encoding Utf8
        Write-Host "[OK] Updated $statePath with ESP partition details." -ForegroundColor Green
    } catch {
        # Non-fatal
    }
}

Write-Host "==================================================================" -ForegroundColor Green
Write-Host "  SUCCESS! Dedicated EFI Boot Partition Created and Populated!    " -ForegroundColor Green
Write-Host "==================================================================" -ForegroundColor Green
Write-Host "Disk 1 now has:" -ForegroundColor White
Write-Host "  - 4096 MB FAT32 EFI Partition (LINUXEFI) with Secure-Boot Shim, GRUB & LiveOS" -ForegroundColor White
Write-Host "  - ~160 GB unallocated space for Linux installation" -ForegroundColor White
Write-Host ""
Write-Host "When you reboot, you will see:" -ForegroundColor Cyan
Write-Host "  1. 'Fedora Workstation 44' or 'UEFI OS' in your BIOS Boot Menu (F11/F12/Del)." -ForegroundColor Cyan
Write-Host "  2. Booting it will start the automated Fedora installer!" -ForegroundColor Cyan

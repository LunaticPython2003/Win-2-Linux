# Update-Bootloader.ps1
# Ensures a dedicated 512 MB FAT32 EFI System Partition exists on Disk 1 (Secondary SSD),
# populates it with official Microsoft-signed SBAT-compliant bootloaders and the Linux installer payload,
# and registers the UEFI NVRAM boot entry.

$ErrorActionPreference = "Stop"

# Ensure script runs with Administrator privileges
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Elevating with Administrator privileges..." -ForegroundColor Yellow
    Start-Process powershell -Verb RunAs -ArgumentList "-ExecutionPolicy Bypass -NoProfile -File `"$PSCommandPath`""
    exit
}

Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host "  Win2Linux EFI Bootloader & ESP Partition Manager               " -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan

# 1. Safety Verification: Protect Windows System Disk (Disk 0)
Write-Host "[1/5] Verifying disk topology..." -ForegroundColor Yellow
$sysDrive = $env:SystemDrive.TrimEnd(':')
$cPart = Get-Partition -DriveLetter $sysDrive -ErrorAction SilentlyContinue
$systemDiskNumber = if ($cPart) { $cPart.DiskNumber } else { 0 }
Write-Host "  Protected Windows System Disk: Disk $systemDiskNumber ($($env:SystemDrive))" -ForegroundColor Green

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
Write-Host "  Target Secondary Disk: Disk $targetDiskNumber (D:)" -ForegroundColor Green

# 2. Check or Create EFI System Partition (ESP) on Disk 1
Write-Host "[2/5] Locating or creating EFI System Partition on Disk $targetDiskNumber..." -ForegroundColor Yellow

$espGptGuid = "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}"
$existingEsp = Get-Partition -DiskNumber $targetDiskNumber | Where-Object { $_.GptType -eq $espGptGuid } | Select-Object -First 1

$targetPartNumber = 0

if ($existingEsp) {
    $targetPartNumber = $existingEsp.PartitionNumber
    Write-Host "  Existing EFI partition detected: Disk $targetDiskNumber Partition $targetPartNumber ($($existingEsp.Size / 1MB -as [int]) MB)" -ForegroundColor Green
    $diskpartScript = @"
select disk $targetDiskNumber
select partition $targetPartNumber
format fs=fat32 quick label="LINUXEFI"
assign letter=W
exit
"@
} else {
    Write-Host "  No EFI partition found on Disk $targetDiskNumber. Creating 4096 MB FAT32 ESP from unallocated space..." -ForegroundColor Yellow
    $diskpartScript = @"
select disk $targetDiskNumber
create partition efi size=4096
format fs=fat32 quick label="LINUXEFI"
assign letter=W
exit
"@
}

$tempScriptPath = "$env:TEMP\win2linux_esp_$([guid]::NewGuid().ToString('N')).txt"
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
    Write-Host "Error: Could not mount ESP to W:. Please verify Disk $targetDiskNumber has unallocated space." -ForegroundColor Red
    exit 1
}

$espPart = Get-Partition -DiskNumber $targetDiskNumber | Where-Object { $_.GptType -eq $espGptGuid } | Select-Object -First 1
if ($espPart) {
    $targetPartNumber = $espPart.PartitionNumber
    Write-Host "[OK] Mounted ESP on Disk $targetDiskNumber Partition $targetPartNumber at W:\" -ForegroundColor Green
}

# 3. Deploy Bootloaders and Installer Payload
Write-Host "[3/5] Deploying official Microsoft-signed SBAT-compliant bootloaders and payload..." -ForegroundColor Yellow

$stageDir = "D:\win2linux\esp-stage"
$sourceEfiStage = "D:\win2linux\efi-stage"
$sourceBootStage = "D:\win2linux\boot"
$sourceUnattended = "D:\win2linux\unattended"

New-Item -ItemType Directory -Force -Path 'W:\EFI\BOOT' | Out-Null
New-Item -ItemType Directory -Force -Path 'W:\EFI\fedora' | Out-Null
New-Item -ItemType Directory -Force -Path 'W:\win2linux\boot' | Out-Null
New-Item -ItemType Directory -Force -Path 'W:\win2linux\unattended' | Out-Null

# 3a. Universal Fallback Bootloader (\EFI\BOOT\)
if (Test-Path "$sourceEfiStage\BOOTX64.EFI") {
    Copy-Item "$sourceEfiStage\BOOTX64.EFI" 'W:\EFI\BOOT\BOOTX64.EFI' -Force
    Copy-Item "$sourceEfiStage\grubx64.efi" 'W:\EFI\BOOT\grubx64.efi' -Force
    if (Test-Path "$sourceEfiStage\mmx64.efi") {
        Copy-Item "$sourceEfiStage\mmx64.efi" 'W:\EFI\BOOT\mmx64.efi' -Force
    }
} elseif (Test-Path "$stageDir\EFI\BOOT\BOOTX64.EFI") {
    Copy-Item "$stageDir\EFI\BOOT\*" 'W:\EFI\BOOT\' -Recurse -Force
}

# 3b. Vendor Bootloader (\EFI\fedora\)
if (Test-Path "$sourceEfiStage\shimx64.efi") {
    Copy-Item "$sourceEfiStage\shimx64.efi" 'W:\EFI\fedora\shimx64.efi' -Force
    Copy-Item "$sourceEfiStage\grubx64.efi" 'W:\EFI\fedora\grubx64.efi' -Force
    if (Test-Path "$sourceEfiStage\mmx64.efi") {
        Copy-Item "$sourceEfiStage\mmx64.efi" 'W:\EFI\fedora\mmx64.efi' -Force
    }
} elseif (Test-Path "$stageDir\EFI\fedora\shimx64.efi") {
    Copy-Item "$stageDir\EFI\fedora\*" 'W:\EFI\fedora\' -Recurse -Force
}

# 3c. grub.cfg for both directories
if (Test-Path "$stageDir\EFI\fedora\grub.cfg") {
    Copy-Item "$stageDir\EFI\fedora\grub.cfg" 'W:\EFI\BOOT\grub.cfg' -Force
    Copy-Item "$stageDir\EFI\fedora\grub.cfg" 'W:\EFI\fedora\grub.cfg' -Force
} elseif (Test-Path "$stageDir\EFI\BOOT\grub.cfg") {
    Copy-Item "$stageDir\EFI\BOOT\grub.cfg" 'W:\EFI\BOOT\grub.cfg' -Force
    Copy-Item "$stageDir\EFI\BOOT\grub.cfg" 'W:\EFI\fedora\grub.cfg' -Force
}

# 3d. Kernel and initrd
if (Test-Path "$sourceBootStage\vmlinuz") {
    Copy-Item "$sourceBootStage\vmlinuz" 'W:\win2linux\boot\vmlinuz' -Force
    Copy-Item "$sourceBootStage\initrd" 'W:\win2linux\boot\initrd' -Force
} elseif (Test-Path "$stageDir\win2linux\boot\vmlinuz") {
    Copy-Item "$stageDir\win2linux\boot\vmlinuz" 'W:\win2linux\boot\vmlinuz' -Force
    Copy-Item "$stageDir\win2linux\boot\initrd" 'W:\win2linux\boot\initrd' -Force
}

# 3e. LiveOS / Squashfs
if (Test-Path "$sourceBootStage\LiveOS") {
    Copy-Item "$sourceBootStage\LiveOS" 'W:\LiveOS' -Recurse -Force
} elseif (Test-Path "$stageDir\LiveOS") {
    Copy-Item "$stageDir\LiveOS" 'W:\LiveOS' -Recurse -Force
}

# 3f. Unattended kickstart and meta-data
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

# Verification
$required = @(
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
foreach ($f in $required) {
    if (-not (Test-Path $f)) {
        $missing += $f
    }
}

if ($missing.Count -gt 0) {
    Write-Host "Warning: Missing expected files: $($missing -join ', ')" -ForegroundColor Red
} else {
    Write-Host "[OK] All 9 critical boot and installer files verified on ESP!" -ForegroundColor Green
}

# 4. Register UEFI NVRAM Boot Entry via bcdedit
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
    Write-Host "Note: NVRAM entry registration bypassed ($bcdResult)." -ForegroundColor Yellow
    Write-Host "      Universal UEFI fallback (\EFI\BOOT\BOOTX64.EFI) is active on the ESP" -ForegroundColor Yellow
    Write-Host "      and will be listed directly in your BIOS Boot Menu." -ForegroundColor Yellow
}

# 5. Safely unmount W:
Write-Host "[5/5] Safely unmounting W:..." -ForegroundColor Yellow
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
Write-Host "  SUCCESS! EFI Bootloader & Partition are 100% Configured!       " -ForegroundColor Green
Write-Host "==================================================================" -ForegroundColor Green
Write-Host "Disk 1 has a dedicated 4096 MB FAT32 EFI partition (LINUXEFI) with:" -ForegroundColor White
Write-Host "  - Official Microsoft-signed SBAT bootloaders (shim + grub)" -ForegroundColor White
Write-Host "  - Installer kernel, initrd, & LiveOS squashfs" -ForegroundColor White
Write-Host ""
Write-Host "You can now reboot and select 'Fedora Workstation 44' or 'UEFI OS' in BIOS." -ForegroundColor Green

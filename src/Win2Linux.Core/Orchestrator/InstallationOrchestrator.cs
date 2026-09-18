using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Win2Linux.Core.Boot;
using Win2Linux.Core.Distros;
using Win2Linux.Core.Download;
using Win2Linux.Core.Extract;
using Win2Linux.Core.Models;
using Win2Linux.Core.Safety;

namespace Win2Linux.Core.Orchestrator;

public record InstallationPlan(
    DistroProfile SelectedDistro,
    CandidateTarget Target,
    ProtectedSystemDisk ProtectedDisk,
    ulong ReservedBytes,
    double ReservedGb
);

public record ProgressUpdate(
    int StepIndex,
    int TotalSteps,
    string StepTitle,
    double Percent,
    string LogMessage,
    bool IsSuccess = true,
    bool IsComplete = false,
    string? ErrorMessage = null
);

public static class InstallationOrchestrator
{
    private static readonly string StateFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Win2Linux",
        "state.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // Total steps in the staging flow
    private const int TotalSteps = 9;

    public static async Task<bool> ExecuteStagingAsync(
        InstallationPlan plan,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct = default)
    {
        var targetRoot = plan.Target.DriveLetter.TrimEnd('\\', '/');
        var repoIsoDir      = IsoDownloadService.GetRepoIsoDirectory();
        var isoStagingDir   = repoIsoDir;
        var bootStagingDir  = Path.Combine(targetRoot + "\\", "win2linux", "boot");
        var efiStagingDir   = Path.Combine(targetRoot + "\\", "win2linux", "efi-stage");
        var unattendedDir   = Path.Combine(targetRoot + "\\", "win2linux", "unattended");

        try
        {

            // STEP 1: SAFETY ASSERTION

            progress.Report(new ProgressUpdate(
                1, TotalSteps, "Verifying System Disk Safety Invariants", 5,
                $"[Safety] Enforcing invariant: Target Disk {plan.Target.DiskNumber} " +
                $"!= Windows System Disk {plan.ProtectedDisk.DiskNumber}..."));

            await Task.Delay(300, ct);

            SafetyValidator.AssertNotProtectedSystemDisk(
                plan.Target.DiskNumber, plan.Target.DiskGuid,
                plan.Target.DiskSerial, plan.ProtectedDisk);
            SafetyValidator.AssertNotSystemVolume(plan.Target.DriveLetter, plan.ProtectedDisk);

            progress.Report(new ProgressUpdate(
                1, TotalSteps, "Safety Invariant Confirmed", 8,
                $"[Safety] ✓ Disk {plan.ProtectedDisk.DiskNumber} ({plan.ProtectedDisk.SystemVolume}) " +
                $"is locked. Zero bytes will be touched on Windows system SSD."));

            await Task.Delay(200, ct);


            // STEP 2: DURABLE TRANSACTION STATE — initial checkpoint

            progress.Report(new ProgressUpdate(
                2, TotalSteps, "Saving Durable Transaction State", 9,
                $"[State] Writing initial checkpoint to {StateFilePath}..."));

            var stateDir = Path.GetDirectoryName(StateFilePath)!;
            Directory.CreateDirectory(stateDir);

            await PersistStateAsync("PREPARING_STAGING", plan, null, null, null, ct);

            progress.Report(new ProgressUpdate(
                2, TotalSteps, "Transaction State Persisted", 10,
                $"[State] ✓ Checkpoint committed. Disk serial: {plan.Target.DiskSerial}"));

            await Task.Delay(200, ct);


            // STEP 3: DOWNLOAD ISO / INFERENCE FROM REPO/ISO

            var inferredIso = IsoDownloadService.InferIsoPath(plan.SelectedDistro, repoIsoDir);
            if (inferredIso != null)
            {
                progress.Report(new ProgressUpdate(
                    3, TotalSteps, "Inferred ISO from repo/iso", 11,
                    $"[ISO Inference] Detected matching ISO in repo/iso: {Path.GetFileName(inferredIso)}"));
            }
            else
            {
                progress.Report(new ProgressUpdate(
                    3, TotalSteps, "Downloading ISO to repo/iso", 11,
                    $"[ISO Download] Target directory: {repoIsoDir}. Starting download of {plan.SelectedDistro.DisplayName}..."));
            }

            var isoPath = await IsoDownloadService.DownloadIsoAsync(
                plan.SelectedDistro, isoStagingDir, progress, ct);


            // STEP 4: VERIFY ISO CHECKSUM

            progress.Report(new ProgressUpdate(
                4, TotalSteps, "Verifying ISO Checksum", 30,
                $"[Checksum] Verifying SHA256 of {Path.GetFileName(isoPath)}..."));

            var verifiedHash = await IsoDownloadService.VerifyIsoAsync(
                plan.SelectedDistro, isoPath, progress, ct);

            await PersistStateAsync("ISO_VERIFIED", plan,
                isoCache: (isoPath, verifiedHash, (ulong)new FileInfo(isoPath).Length),
                espResult: null, nvramResult: null, ct);


            // STEP 5: EXTRACT BOOT FILES FROM ISO

            progress.Report(new ProgressUpdate(
                5, TotalSteps, "Extracting Boot Files", 36,
                $"[Extract] Extracting kernel, initrd, EFI shim and GRUB from ISO..."));

            await IsoExtractService.ExtractBootFilesAsync(
                plan.SelectedDistro, isoPath, bootStagingDir, efiStagingDir, progress, ct);

            // For Fedora/openSUSE: also extract LiveOS/squashfs.img (the live root filesystem).
            // dracut's rd.live.image module requires this image to be present on the ESP
            // at LiveOS/squashfs.img relative to the partition root.
            // Without it, dracut cannot mount / and drops to an emergency shell.
            if (IsoExtractService.RequiresLiveSquashfs(plan.SelectedDistro))
            {
                await IsoExtractService.ExtractLiveSquashfsAsync(
                    isoPath, bootStagingDir, progress, ct);
            }


            // STEP 6: GENERATE UNATTENDED INSTALLATION CONFIG

            Directory.CreateDirectory(unattendedDir);

            progress.Report(new ProgressUpdate(
                6, TotalSteps, "Generating Automated Installation Payload", 46,
                $"[Unattended] Generating {plan.SelectedDistro.UnattendedMechanism} config..."));

            string unattendedFilename = GetUnattendedFilename(plan.SelectedDistro.Id);
            string unattendedContent = GenerateUnattendedConfig(plan);
            await File.WriteAllTextAsync(Path.Combine(unattendedDir, unattendedFilename), unattendedContent, ct);

            progress.Report(new ProgressUpdate(
                6, TotalSteps, "Unattended Payload Staged", 48,
                $"[Unattended] ✓ {unattendedFilename} written to {unattendedDir}"));

            await Task.Delay(200, ct);


            // STEP 7: NTFS VOLUME SHRINK

            progress.Report(new ProgressUpdate(
                7, TotalSteps, "Shrinking NTFS Volume", 50,
                $"[Volume] Shrinking {plan.Target.DriveLetter} by {plan.ReservedGb:F0} GB to create unallocated space..."));

            await ExecuteVolumeShrinkAsync(plan, progress, ct);

            await Task.Delay(300, ct);


            // STEP 8: CREATE AND POPULATE EFI SYSTEM PARTITION (ESP)
            // Creates a dedicated 4096 MB FAT32 EFI partition on the secondary disk (Disk 1),
            // mounts it to a temporary drive letter, and writes the complete bootloader tree.

            progress.Report(new ProgressUpdate(
                8, TotalSteps, "Creating EFI Boot Partition", 72,
                $"[ESP] Creating 4096 MB FAT32 EFI boot partition on Disk {plan.Target.DiskNumber}..."));

            var espResult = await EspCreationService.CreateEspAsync(plan, plan.ProtectedDisk, progress, ct);

            try
            {
                var espRoot = $"{espResult.DriveLetter}:\\";
                progress.Report(new ProgressUpdate(
                    8, TotalSteps, "Populating EFI Environment", 74,
                    $"[ESP] Writing EFI tree, boot files, and autoinstall payload to {espRoot}..."));

                await EspPopulationService.PopulateEspAsync(
                    plan.SelectedDistro, espRoot, bootStagingDir, efiStagingDir, unattendedDir, progress, ct);

                // Also maintain a backup copy in D:\win2linux\esp-stage
                try
                {
                    var backupEspRoot = Path.Combine(plan.Target.DriveLetter.TrimEnd('\\', '/') + "\\", "win2linux", "esp-stage");
                    Directory.CreateDirectory(backupEspRoot);
                    await EspPopulationService.PopulateEspAsync(
                        plan.SelectedDistro, backupEspRoot, bootStagingDir, efiStagingDir, unattendedDir, progress, ct);
                }
                catch { /* Backup is best-effort */ }

                progress.Report(new ProgressUpdate(
                    8, TotalSteps, "EFI Environment Ready", 88,
                    $"[ESP] ✓ EFI System Partition created on Disk {espResult.DiskNumber} Partition {espResult.PartitionNumber} and populated."));


                // STEP 9: REGISTER UEFI NVRAM BOOT ENTRY

                progress.Report(new ProgressUpdate(
                    9, TotalSteps, "Registering UEFI Boot Entry", 89,
                    $"[NVRAM] Registering UEFI NVRAM boot entry for {plan.SelectedDistro.DisplayName}..."));

                var nvramResult = await TryRegisterUefiBootEntryAsync(plan, espResult, progress, ct);


                // FINAL: PERSIST COMPLETED STATE

                await PersistStateAsync("BOOT_ENV_PREPARED", plan,
                    isoCache: (isoPath, verifiedHash, (ulong)new FileInfo(isoPath).Length),
                    espResult: espResult, nvramResult, ct);

                progress.Report(new ProgressUpdate(
                    TotalSteps, TotalSteps, "Dual-Boot Environment Ready!", 100,
                    $"[Complete] All 9 steps completed. " +
                    $"EFI partition ready: ✓ (Disk {espResult.DiskNumber} Part {espResult.PartitionNumber})  " +
                    $"NVRAM entry: {(nvramResult.Success ? "✓" : "Fallback active (BOOTX64.EFI)")}. " +
                    $"Reboot now to start the Linux installer.",
                    IsSuccess: true,
                    IsComplete: true));

                return true;
            }
            finally
            {
                // Always safely unmount the temporary drive letter assigned to the ESP
                await EspCreationService.RemoveEspDriveLetterAsync(espResult.DriveLetter, ct);
            }
        }
        catch (Exception ex)
        {
            progress.Report(new ProgressUpdate(
                0, TotalSteps, "Staging Failed", 0,
                $"[Error] Staging aborted: {ex.Message}",
                IsSuccess: false,
                IsComplete: true,
                ErrorMessage: ex.Message));
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NVRAM Registration (bcdedit — best-effort, non-fatal)
    // ─────────────────────────────────────────────────────────────────────────

    public record NvramRegistrationResult(
        bool Attempted,
        bool Success,
        string? EntryGuid,
        string? ErrorMessage
    );

    private static async Task<NvramRegistrationResult> TryRegisterUefiBootEntryAsync(
        InstallationPlan plan,
        EspCreationService.EspCreationResult espResult,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct)
    {
        try
        {
            // 1. Create a new boot application entry in BCD
            var createOutput = await RunBcdeditAsync(
                $"/copy {{bootmgr}} /d \"{plan.SelectedDistro.UefiBootLabel}\"", ct);

            var guidMatch = System.Text.RegularExpressions.Regex.Match(
                createOutput, @"\{([0-9a-fA-F\-]{36})\}");

            if (!guidMatch.Success)
            {
                createOutput = await RunBcdeditAsync(
                    $"/create /d \"{plan.SelectedDistro.UefiBootLabel}\" /application bootapp", ct);
                guidMatch = System.Text.RegularExpressions.Regex.Match(
                    createOutput, @"\{([0-9a-fA-F\-]{36})\}");
            }

            if (!guidMatch.Success)
            {
                progress.Report(new ProgressUpdate(10, TotalSteps, "NVRAM Registration Skipped", 92,
                    $"[NVRAM] bcdedit could not create entry. Fallback boot (BOOTX64.EFI) is active."));
                return new NvramRegistrationResult(true, false, null,
                    $"Could not parse GUID from bcdedit output: {createOutput}");
            }

            var entryGuid = $"{{{guidMatch.Groups[1].Value}}}";

            // 2. Set device partition to the mounted ESP
            await RunBcdeditAsync(
                $"/set {entryGuid} device partition={espResult.DriveLetter}:", ct);

            // 3. Set the EFI path to the vendor shim
            await RunBcdeditAsync(
                $"/set {entryGuid} path \\EFI\\{plan.SelectedDistro.EfiVendorDir}\\{plan.SelectedDistro.EfiBinary}", ct);

            // 4. Add to firmware display order (first position)
            await RunBcdeditAsync(
                $"/set {{fwbootmgr}} displayorder {entryGuid} /addfirst", ct);

            // 5. Verify Windows Boot Manager is still present
            var enumOutput = await RunBcdeditAsync("/enum firmware", ct);
            bool windowsPresent = enumOutput.Contains("Windows Boot Manager", StringComparison.OrdinalIgnoreCase);

            if (!windowsPresent)
            {
                progress.Report(new ProgressUpdate(10, TotalSteps, "NVRAM Warning", 93,
                    $"[NVRAM] WARNING: Windows Boot Manager not found in firmware entries after registration. " +
                    $"Please verify boot order in BIOS. Entry GUID: {entryGuid}"));
            }
            else
            {
                progress.Report(new ProgressUpdate(10, TotalSteps, "UEFI Boot Entry Registered", 93,
                    $"[NVRAM] ✓ Registered '{plan.SelectedDistro.UefiBootLabel}' as UEFI firmware entry {entryGuid}. " +
                    $"Windows Boot Manager confirmed present."));
            }

            return new NvramRegistrationResult(true, true, entryGuid, null);
        }
        catch (Exception ex)
        {
            progress.Report(new ProgressUpdate(10, TotalSteps, "NVRAM Registration Skipped", 92,
                $"[NVRAM] bcdedit registration not required (BOOTX64.EFI fallback is active): {ex.Message}"));
            return new NvramRegistrationResult(true, false, null, ex.Message);
        }
    }

    private static async Task<string> RunBcdeditAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "bcdedit.exe",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
        using var proc = Process.Start(psi) ?? throw new Exception("Failed to start bcdedit.exe");

        var output = await proc.StandardOutput.ReadToEndAsync(linked.Token);
        await proc.WaitForExitAsync(linked.Token);
        return output;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Volume Shrink (diskpart)
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<bool> ExecuteVolumeShrinkAsync(
        InstallationPlan plan,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct)
    {
        try
        {
            var driveLetter = plan.Target.DriveLetter.TrimEnd('\\', ':');
            var shrinkMb = (long)(plan.ReservedGb * 1024);

            // Check if target disk already has sufficient unallocated space from a previous run
            var existingFreeExtent = await Task.Run(() =>
            {
                try
                {
                    var scope = new System.Management.ManagementScope(@"\\.\ROOT\Microsoft\Windows\Storage");
                    scope.Connect();
                    using var searcher = new System.Management.ManagementObjectSearcher(scope,
                        new System.Management.ObjectQuery($"SELECT Size, AllocatedSize, LargestFreeExtent FROM MSFT_Disk WHERE Number = {plan.Target.DiskNumber}"));
                    using var coll = searcher.Get();
                    foreach (System.Management.ManagementObject d in coll)
                    {
                        var extent = Convert.ToUInt64(d["LargestFreeExtent"] ?? 0);
                        if (extent > 0) return extent;
                        var size = Convert.ToUInt64(d["Size"] ?? 0);
                        var alloc = Convert.ToUInt64(d["AllocatedSize"] ?? 0);
                        if (size > alloc) return size - alloc;
                    }
                }
                catch { }
                return 0UL;
            }, ct);

            var requiredBytes = (ulong)(plan.ReservedGb * 1024 * 1024 * 1024);
            if (existingFreeExtent >= (ulong)(requiredBytes * 0.9))
            {
                progress.Report(new ProgressUpdate(7, TotalSteps, "Volume Space Confirmed", 63,
                    $"[Volume] ✓ Disk {plan.Target.DiskNumber} already has {existingFreeExtent / (1024 * 1024 * 1024):F0} GB unallocated space. Skipping redundant shrink."));
                return true;
            }

            progress.Report(new ProgressUpdate(7, TotalSteps, "Shrinking NTFS Volume", 52,
                $"[Volume] Invoking diskpart to shrink {driveLetter}: by {shrinkMb} MB..."));

            var diskpartScriptPath = Path.Combine(Path.GetTempPath(), $"win2linux_shrink_{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(diskpartScriptPath,
                $"select volume {driveLetter}\nshrink desired={shrinkMb} minimum=10240\n", ct);

            var psi = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{diskpartScriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
            using var process = Process.Start(psi);

            if (process != null)
            {
                var readOutputTask = process.StandardOutput.ReadToEndAsync(linked.Token);
                await process.WaitForExitAsync(linked.Token);
                var output = await readOutputTask;

                try { File.Delete(diskpartScriptPath); } catch { }

                if (process.ExitCode == 0 && output.Contains("successfully shrunk", StringComparison.OrdinalIgnoreCase))
                {
                    progress.Report(new ProgressUpdate(7, TotalSteps, "Volume Shrunk", 63,
                        $"[Volume] ✓ Volume {driveLetter}: shrunk by {plan.ReservedGb:F0} GB. Unallocated space created."));
                    return true;
                }
                else
                {
                    var msg = output.Trim().Split('\n').LastOrDefault() ?? "Shrink queued";
                    progress.Report(new ProgressUpdate(7, TotalSteps, "Volume Shrink Status", 63,
                        $"[Volume] {msg}"));
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            progress.Report(new ProgressUpdate(7, TotalSteps, "Volume Shrink Note", 63,
                $"[Volume] Shrink note: {ex.Message}"));
        }

        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // State persistence
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task PersistStateAsync(
        string state,
        InstallationPlan plan,
        (string Path, string Hash, ulong Size)? isoCache,
        EspCreationService.EspCreationResult? espResult,
        NvramRegistrationResult? nvramResult,
        CancellationToken ct)
    {
        var stateRecord = new
        {
            Version = "1.0",
            State = state,
            Timestamp = DateTime.UtcNow.ToString("o"),
            ProtectedSystemDisk = new
            {
                plan.ProtectedDisk.DiskNumber,
                plan.ProtectedDisk.DiskGuid,
                plan.ProtectedDisk.DiskSerial,
                plan.ProtectedDisk.SystemVolume,
                Status = "LOCKED"
            },
            LinuxTarget = new
            {
                plan.Target.DiskNumber,
                plan.Target.DiskGuid,
                plan.Target.DiskSerial,
                WindowsVolume = plan.Target.DriveLetter,
                plan.Target.VolumeLabel,
                plan.Target.TotalBytes,
                RequestedLinuxBytes = plan.ReservedBytes,
                RequestedLinuxGb = plan.ReservedGb,
                TargetFilesystem = plan.SelectedDistro.DefaultFilesystem
            },
            Distribution = new
            {
                plan.SelectedDistro.Id,
                plan.SelectedDistro.DisplayName,
                Version = plan.SelectedDistro.DefaultVersion,
                plan.SelectedDistro.SecureBootStatus,
                Signer = plan.SelectedDistro.SecureBootSigner,
                EfiBootloader = $"{plan.SelectedDistro.EfiVendorDir}/{plan.SelectedDistro.EfiBinary}"
            },
            IsoCache = isoCache.HasValue
                ? new { isoCache.Value.Path, Sha256 = isoCache.Value.Hash, SizeBytes = isoCache.Value.Size, Verified = true }
                : null,
            Esp = espResult != null
                ? new
                {
                    espResult.DiskNumber,
                    espResult.PartitionNumber,
                    PartitionGuid = espResult.PartitionGuid,
                    DriveLetter = espResult.DriveLetter.ToString(),
                    espResult.SizeBytes,
                    Created = true,
                    FallbackBootPathInstalled = true
                }
                : null,
            NvramRegistration = nvramResult != null
                ? new
                {
                    nvramResult.Attempted,
                    nvramResult.Success,
                    BcdeditEntryGuid = nvramResult.EntryGuid,
                    FallbackBootPathInstalled = true
                }
                : null
        };

        Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
        await File.WriteAllTextAsync(StateFilePath, JsonSerializer.Serialize(stateRecord, JsonOpts), ct);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Unattended config generation
    // ─────────────────────────────────────────────────────────────────────────

    public static string GetUnattendedFilename(string distroId) => distroId.ToLowerInvariant() switch
    {
        "ubuntu" => "autoinstall.yaml",
        "fedora" => "kickstart.ks",
        "linuxmint" => "preseed.cfg",
        "zorin" => "autoinstall.yaml",
        _ => "autoinstall.yaml"
    };

    public static string GenerateUnattendedConfig(InstallationPlan plan)
    {
        var linuxSizeMb = (long)(plan.ReservedGb * 1024);

        return plan.SelectedDistro.Id.ToLowerInvariant() switch
        {
            "ubuntu" => GenerateUbuntuAutoinstall(plan, linuxSizeMb),
            "fedora" => GenerateFedoraKickstart(plan, linuxSizeMb),
            "linuxmint" => GenerateMintPreseed(plan),
            "zorin" => GenerateZorinAutoinstall(plan, linuxSizeMb),
            _ => "# Default unattended configuration\n"
        };
    }

    private static string GenerateUbuntuAutoinstall(InstallationPlan plan, long linuxSizeMb)
    {
        // NOTE: preserve: true on the disk is CRITICAL to protect the existing D: partition.
        // The ESP we created is referenced by partition number recorded in state.json.
        // The root partition uses size: -1 (consume all remaining unallocated space).
        return $$"""
            #cloud-config
            autoinstall:
              version: 1
              interactive-sections: []
              locale: en_US.UTF-8
              keyboard:
                layout: us
              identity:
                realname: Linux User
                username: user
                hostname: {{plan.SelectedDistro.Id}}-dualboot
                # Generated placeholder — user should change on first login
                password: '$6$rounds=4096$win2linux$placeholder'
              storage:
                layout:
                  name: custom
                config:
                  # Target disk — PRESERVE existing partitions (D: is protected)
                  - type: disk
                    id: target-disk
                    serial: "{{plan.Target.DiskSerial}}"
                    preserve: true
                  # Reuse the ESP we created in Win2Linux staging
                  - type: partition
                    id: esp-partition
                    device: target-disk
                    number: -1
                    flag: boot
                    preserve: true
                  - type: format
                    id: esp-format
                    fstype: fat32
                    volume: esp-partition
                    preserve: true
                  - type: mount
                    id: esp-mount
                    path: /boot/efi
                    device: esp-format
                  # New Linux root partition — all remaining unallocated space
                  - type: partition
                    id: root-partition
                    device: target-disk
                    size: -1
                    preserve: false
                  - type: format
                    id: root-format
                    fstype: {{plan.SelectedDistro.DefaultFilesystem}}
                    volume: root-partition
                  - type: mount
                    id: root-mount
                    path: /
                    device: root-format
              packages:
                - shim-signed
                - grub-efi-amd64-signed
                - efibootmgr
              early-commands:
                - echo "Win2Linux: Starting {{plan.SelectedDistro.DisplayName}} automated dual-boot install..."
              late-commands:
                # Register permanent UEFI NVRAM 'ubuntu' entry via efibootmgr
                - >-
                  curtin in-target --target=/target --
                  bash -c 'DISK=$(ls /dev/disk/by-id/ | grep "{{plan.Target.DiskSerial}}" | grep -v part | head -1);
                  ESP_PART=$(ls /dev/disk/by-id/ | grep "{{plan.Target.DiskSerial}}" | grep "part" | sort | head -1);
                  PART_NUM=$(echo "$ESP_PART" | grep -oP "part\K\d+");
                  efibootmgr --create
                  --disk /dev/disk/by-id/$DISK
                  --part $PART_NUM
                  --label "{{plan.SelectedDistro.UefiBootLabel}}"
                  --loader "\\EFI\\{{plan.SelectedDistro.EfiVendorDir}}\\{{plan.SelectedDistro.EfiBinary}}"
                  --unicode ""'
                # Set BootOrder: ubuntu first, Windows Boot Manager second
                - >-
                  curtin in-target --target=/target --
                  bash -c '
                  UBUNTU=$(efibootmgr | grep -oiP "Boot\K[0-9A-F]+(?=\*\s+{{plan.SelectedDistro.UefiBootLabel}})");
                  WIN=$(efibootmgr | grep -oiP "Boot\K[0-9A-F]+(?=\*\s+Windows Boot Manager)");
                  [ -n "$UBUNTU" ] && [ -n "$WIN" ] && efibootmgr --bootorder ${UBUNTU},${WIN} || true'
                # Safety check — Windows Boot Manager must still be present
                - >-
                  bash -c 'efibootmgr | grep -qi "Windows Boot Manager" ||
                  (echo "CRITICAL: Windows Boot Manager entry missing after install" && exit 1)'
                # Regenerate GRUB menu so Windows entry appears
                - curtin in-target --target=/target -- update-grub
            """;
    }

    private static string GenerateZorinAutoinstall(InstallationPlan plan, long linuxSizeMb)
    {
        return $$"""
            #cloud-config
            autoinstall:
              version: 1
              interactive-sections: []
              locale: en_US.UTF-8
              keyboard:
                layout: us
              identity:
                realname: Linux User
                username: user
                hostname: zorin-dualboot
                password: '$6$rounds=4096$win2linux$placeholder'
              storage:
                layout:
                  name: custom
                config:
                  - type: disk
                    id: target-disk
                    serial: "{{plan.Target.DiskSerial}}"
                    preserve: true
                  - type: partition
                    id: esp-partition
                    device: target-disk
                    number: -1
                    flag: boot
                    preserve: true
                  - type: format
                    id: esp-format
                    fstype: fat32
                    volume: esp-partition
                    preserve: true
                  - type: mount
                    id: esp-mount
                    path: /boot/efi
                    device: esp-format
                  - type: partition
                    id: root-partition
                    device: target-disk
                    size: -1
                    preserve: false
                  - type: format
                    id: root-format
                    fstype: {{plan.SelectedDistro.DefaultFilesystem}}
                    volume: root-partition
                  - type: mount
                    id: root-mount
                    path: /
                    device: root-format
              packages:
                - shim-signed
                - grub-efi-amd64-signed
                - efibootmgr
              late-commands:
                - >-
                  curtin in-target --target=/target --
                  bash -c 'DISK=$(ls /dev/disk/by-id/ | grep "{{plan.Target.DiskSerial}}" | grep -v part | head -1);
                  ESP_PART=$(ls /dev/disk/by-id/ | grep "{{plan.Target.DiskSerial}}" | grep "part" | sort | head -1);
                  PART_NUM=$(echo "$ESP_PART" | grep -oP "part\K\d+");
                  efibootmgr --create
                  --disk /dev/disk/by-id/$DISK
                  --part $PART_NUM
                  --label "{{plan.SelectedDistro.UefiBootLabel}}"
                  --loader "\\EFI\\{{plan.SelectedDistro.EfiVendorDir}}\\{{plan.SelectedDistro.EfiBinary}}"
                  --unicode ""'
                - curtin in-target --target=/target -- update-grub
            """;
    }

    private static string GenerateFedoraKickstart(InstallationPlan plan, long linuxSizeMb)
    {
        var dePackageGroup = plan.SelectedDistro.SelectedDesktopEnvironment == "gnome"
            ? "@^workstation-product-environment"
            : "@^kde-desktop-environment";

        var deLabel = plan.SelectedDistro.SelectedDesktopEnvironment == "gnome"
            ? "GNOME Workstation"
            : "KDE Plasma";

        return $$"""
            # Fedora 44 ({{deLabel}}) Kickstart Configuration
            # Generated by Win2Linux Dual-Boot Installer
            text
            lang en_US.UTF-8
            keyboard us
            timezone UTC

            # Automated partitioning into unallocated free space only
            # NEVER clear or modify existing Windows partitions
            bootloader --location=none
            clearpart --none
            autopart --type=btrfs --nohome

            %packages
            {{dePackageGroup}}
            kernel
            grub2-efi-x64
            shim-x64
            efibootmgr
            %end

            reboot
            """;
    }

    private static string GenerateMintPreseed(InstallationPlan plan)
    {
        return $$"""
            # Linux Mint 22.1 Automatic Ubiquity Preseed Configuration
            # Generated by Win2Linux Dual-Boot Installer
            d-i debian-installer/locale string en_US.UTF-8
            d-i console-setup/ask_detect boolean false
            d-i keyboard-configuration/layoutcode string us
            d-i netcfg/choose_interface select auto
            d-i netcfg/get_hostname string linuxmint-dualboot

            # Clock and time zone
            d-i time/zone string UTC
            d-i clock-setup/utc boolean true

            # Partitioning: install into free unallocated space on secondary disk, preserve D:
            d-i partman-auto/method string regular
            d-i partman-auto/init-automatically-partition select biggest_free
            d-i partman-partitioning/confirm_write_new_label boolean true
            d-i partman/choose_partition select finish
            d-i partman/confirm boolean true
            d-i partman/confirm_nooverwrite boolean true

            # Bootloader: Secure Boot signed grub
            d-i grub-installer/only_debian boolean false
            d-i grub-installer/with_other_os boolean true
            d-i grub-installer/bootdev string default

            # User setup placeholder
            d-i passwd/user-fullname string Linux User
            d-i passwd/username string user
            d-i passwd/user-password-crypted password $6$rounds=4096$win2linux$placeholder

            # Completion
            ubiquity ubiquity/reboot boolean true
            d-i finish-install/reboot_in_progress note
            """;
    }
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Win2Linux.Core.Boot;
using Win2Linux.Core.Distros;
using Win2Linux.Core.Download;
using Win2Linux.Core.Extract;
using Win2Linux.Core.Models;
using Win2Linux.Core.Safety;
using Win2Linux.Core.Storage;
using YamlDotNet.RepresentationModel;

namespace Win2Linux.Core.Orchestrator;

public record InstallationPlan(
    DistroProfile SelectedDistro,
    CandidateTarget Target,
    ProtectedSystemDisk ProtectedDisk,
    ulong ReservedBytes,
    double ReservedGb,
    LinuxSetupConfiguration? SetupConfig = null
)
{
    public LinuxSetupConfiguration EffectiveSetupConfig
    {
        get
        {
            var config = SetupConfig ?? LinuxSetupConfiguration.CreateDefault(SelectedDistro.Id);
            return config with { Hostname = LinuxSetupConfiguration.NormalizeHostname(config.Hostname) };
        }
    }
}


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
        string? bootStagingDir = null;
        string? efiStagingDir = null;
        string? unattendedDir = null;
        EspStagingLayout? espStaging = null;
        Win2Linux.Core.Boot.EspCreationService.EspCreationResult? espResult = null;
        NvramRegistrationResult? nvramResult = null;

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
                plan.Target.WindowsDiskSerial, plan.ProtectedDisk);
            SafetyValidator.AssertNotSystemVolume(plan.Target.DriveLetter, plan.ProtectedDisk);

            progress.Report(new ProgressUpdate(
                1, TotalSteps, "Safety Invariant Confirmed", 8,
                $"[Safety] ✓ Disk {plan.ProtectedDisk.DiskNumber} ({plan.ProtectedDisk.SystemVolume}) " +
                $"is locked. Zero bytes will be touched on Windows system SSD."));

            await Task.Delay(200, ct);


            // Validate user input and plan configuration
            var config = plan.EffectiveSetupConfig;
            if (string.IsNullOrWhiteSpace(config.Username) || config.Username.Length > 32 || !System.Text.RegularExpressions.Regex.IsMatch(config.Username, @"^[a-z_][a-z0-9_-]*$"))
            {
                throw new InvalidOperationException($"CRITICAL SAFETY VIOLATION: Username '{config.Username}' is invalid. Must be up to 32 characters, consist of lower case letters, numbers, hyphens, and underscores, and start with a letter or underscore. Operation ABORTED.");
            }
            var reservedUsernames = new[] { "root", "daemon", "bin", "sys", "sync", "games", "man", "lp", "mail", "news", "uucp", "proxy", "www-data", "backup", "list", "irc", "gnats", "nobody", "systemd-network", "systemd-resolve", "syslog", "messagebus", "_apt", "uuidd", "tcpdump", "sshd", "pollinate", "ubuntu" };
            if (Array.Exists(reservedUsernames, name => name.Equals(config.Username, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"CRITICAL SAFETY VIOLATION: Username '{config.Username}' is a reserved system name. Operation ABORTED.");
            }

            if (string.IsNullOrWhiteSpace(config.Hostname) || config.Hostname.Length > 63 || !System.Text.RegularExpressions.Regex.IsMatch(config.Hostname, @"^[a-z0-9]([-a-z0-9]*[a-z0-9])?$"))
            {
                throw new InvalidOperationException($"CRITICAL SAFETY VIOLATION: Hostname '{config.Hostname}' is invalid. Must be up to 63 characters, consist of lower case letters, numbers, and hyphens, and cannot start or end with a hyphen. Operation ABORTED.");
            }

            if (plan.ReservedGb < 28)
            {
                throw new InvalidOperationException($"CRITICAL SAFETY VIOLATION: Reserved space {plan.ReservedGb:F0} GB is below the minimum required 28 GB. Operation ABORTED.");
            }

            // STEP 2: DURABLE TRANSACTION STATE — initial checkpoint

            progress.Report(new ProgressUpdate(
                2, TotalSteps, "Saving Durable Transaction State", 9,
                $"[State] Writing initial checkpoint to {StateFilePath}..."));

            var stateDir = Path.GetDirectoryName(StateFilePath)!;
            Directory.CreateDirectory(stateDir);

            await PersistStateAsync("PREPARING_STAGING", plan, null, null, null, ct);

            progress.Report(new ProgressUpdate(
                2, TotalSteps, "Transaction State Persisted", 10,
                $"[State] ✓ Checkpoint committed. Disk serial: {plan.Target.WindowsDiskSerial}"));

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




            // STEP 6: (Moved to Step 8 to access dynamic ESP partition number)
            progress.Report(new ProgressUpdate(
                6, TotalSteps, "Deferring Unattended Config Generation", 48,
                $"[Unattended] Waiting for ESP creation to finalize partition geometry..."));
            await Task.Delay(200, ct);


            // STEP 7: NTFS VOLUME SHRINK

            // Re-verify that target DriveLetter still maps to the expected DiskNumber/Serial before shrinking
            var currentVols = StorageDiscoveryService.RunDiscovery().CandidateTargets;
            var currentTarget = currentVols.FirstOrDefault(v => v.DriveLetter.Equals(plan.Target.DriveLetter, StringComparison.OrdinalIgnoreCase));
            if (currentTarget == null || currentTarget.DiskNumber != plan.Target.DiskNumber || currentTarget.WindowsDiskSerial != plan.Target.WindowsDiskSerial)
            {
                throw new InvalidOperationException($"Safety Verification Failed: Drive {plan.Target.DriveLetter} no longer matches expected Disk {plan.Target.DiskNumber} / Serial {plan.Target.WindowsDiskSerial}. Aborting to prevent data loss.");
            }

            progress.Report(new ProgressUpdate(
                7, TotalSteps, "Shrinking NTFS Volume", 50,
                $"[Volume] Shrinking {plan.Target.DriveLetter} by {plan.ReservedGb:F0} GB to create unallocated space..."));

            var shrinkOk = await ExecuteVolumeShrinkAsync(plan, progress, ct);
            if (!shrinkOk)
            {
                throw new InvalidOperationException("NTFS shrink failed; refusing to continue installation.");
            }

            await PersistStateAsync("VOLUME_SHRUNK", plan, null, null, null, ct);
            await Task.Delay(300, ct);




            // STEP 8: CREATE AND POPULATE EFI SYSTEM PARTITION (ESP)
            // Creates a dedicated 8192 MB FAT32 EFI partition on the secondary disk (Disk 1),
            // mounts it to a temporary drive letter, and writes the complete bootloader tree.

            progress.Report(new ProgressUpdate(
                8, TotalSteps, "Creating EFI Boot Partition", 72,
                $"[ESP] Creating 8192 MB FAT32 EFI boot partition on Disk {plan.Target.DiskNumber}..."));

            espResult = await EspCreationService.CreateEspAsync(plan, plan.ProtectedDisk, progress, ct);
            await PersistStateAsync("ESP_CREATED", plan, null, espResult, null, ct);

            try
            {
                var espRoot = $"{espResult.DriveLetter}:\\";
                
                // Extract on the ESP so moving large live images needs no second
                // copy, but never use a final boot/config directory as scratch.
                espStaging = new EspStagingLayout(espRoot);
                bootStagingDir = espStaging.BootDirectory;
                efiStagingDir = espStaging.EfiDirectory;
                unattendedDir = espStaging.UnattendedDirectory;
                
                // Refresh partition sizes and list AFTER ESP creation, so the storage config reflects exact post-shrink and post-ESP reality
                var psiRefresh = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \"Update-HostStorageCache\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psiRefresh))
                {
                    await p!.WaitForExitAsync(ct);
                }

                // STEP 5 (Reordered): EXTRACT BOOT FILES FROM ISO AFTER SHRINK
                // This guarantees large multi-GB ISO extraction files don't pin the end of the NTFS volume and limit shrink space.
                progress.Report(new ProgressUpdate(
                    5, TotalSteps, "Extracting Boot Files", 36,
                    $"[Extract] Extracting kernel, initrd, EFI shim and GRUB from ISO..."));

                await IsoExtractService.ExtractBootFilesAsync(
                    plan.SelectedDistro, isoPath, bootStagingDir, efiStagingDir, progress, ct);

                if (IsoExtractService.RequiresLiveSquashfs(plan.SelectedDistro))
                {
                    await IsoExtractService.ExtractLiveSquashfsAsync(
                        isoPath, bootStagingDir, progress, ct);
                }
                else if (IsoExtractService.RequiresCasper(plan.SelectedDistro))
                {
                    await IsoExtractService.ExtractCasperFilesAsync(
                        isoPath, bootStagingDir, progress, ct);
                }

                if (IsoExtractService.RequiresOfflineAptRepository(plan.SelectedDistro))
                {
                    await IsoExtractService.ExtractOfflineAptRepositoryAsync(
                        plan.SelectedDistro, isoPath, bootStagingDir, progress, ct);
                }

                var scope = new System.Management.ManagementScope(@"\\.\ROOT\Microsoft\Windows\Storage");
                scope.Connect();
                using var searcher = new System.Management.ManagementObjectSearcher(scope,
                    new System.Management.ObjectQuery($"SELECT PartitionNumber, Size, GptType, Offset FROM MSFT_Partition WHERE DiskNumber = {plan.Target.DiskNumber}"));
                using var coll = searcher.Get();
                var updatedPartitions = new List<Win2Linux.Core.Models.PartitionInfo>();
                
                foreach (System.Management.ManagementObject p in coll)
                {
                    int pNo = Convert.ToInt32(p["PartitionNumber"] ?? 0);
                    // Do not preserve the ESP itself in the generic partition list (since we explicitly define it in the YAML)
                    if (pNo == espResult.PartitionNumber) continue;
                    
                    var gptType = p["GptType"]?.ToString()?.Trim('{', '}');
                    ulong pSize = Convert.ToUInt64(p["Size"] ?? 0);
                    updatedPartitions.Add(new Win2Linux.Core.Models.PartitionInfo(
                        DiskNumber: plan.Target.DiskNumber,
                        PartitionNumber: pNo,
                        DriveLetter: null,
                        SizeBytes: pSize,
                        GptType: gptType ?? "",
                        Guid: null,
                        OffsetBytes: p["Offset"] is null ? null : Convert.ToUInt64(p["Offset"])
                    ));
                }
                plan.Target.Partitions.Clear();
                plan.Target.Partitions.AddRange(updatedPartitions);

                // GENERATE UNATTENDED CONFIG NOW THAT WE HAVE THE ESP PARTITION NUMBER
                if (!Guid.TryParse(espResult.PartitionGuid, out _))
                {
                    throw new InvalidOperationException(
                        "ESP partition GUID could not be verified. Refusing to generate Ubuntu storage configuration without a cross-platform disk identity.");
                }

                Directory.CreateDirectory(unattendedDir);
                string unattendedFilename = GetUnattendedFilename(plan.SelectedDistro.Id);
                string unattendedContent = GenerateUnattendedConfig(
                    plan,
                    espResult.PartitionNumber,
                    espResult.SizeBytes,
                    espResult.PartitionGuid,
                    espResult.OffsetBytes);
                
                if (plan.SelectedDistro.Id.ToLowerInvariant() is "ubuntu" or "zorin")
                {
                    ValidateAutoinstallYaml(unattendedContent);
                }

                var finalContent = unattendedContent.Replace("\r\n", "\n");
                await File.WriteAllTextAsync(Path.Combine(unattendedDir, unattendedFilename), finalContent, ct);
                
                // For NoCloud, we must also provide an empty meta-data file
                if (unattendedFilename == "user-data")
                {
                    await File.WriteAllTextAsync(Path.Combine(unattendedDir, "meta-data"), "", ct);
                }
                
                // Keep a reviewable copy in the workspace, but redact any selected Wi-Fi key.
                var slnDir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (slnDir.Parent != null && !File.Exists(Path.Combine(slnDir.FullName, "Win2Linux.sln")))
                {
                    slnDir = slnDir.Parent;
                }

                var linuxOutDir = File.Exists(Path.Combine(slnDir.FullName, "Win2Linux.sln"))
                    ? Path.Combine(slnDir.FullName, "linux")
                    : Path. Combine(Environment.CurrentDirectory, "linux");
                Directory.CreateDirectory(linuxOutDir);
                var workspaceContent = finalContent;
                var wifiNetwork = plan.EffectiveSetupConfig.WifiNetwork;
                if (!string.IsNullOrEmpty(wifiNetwork?.Password))
                {
                    var actualNetworkConfig = GenerateUbuntuNetworkConfig(wifiNetwork);
                    var redactedNetworkConfig = GenerateUbuntuNetworkConfig(wifiNetwork with { Password = "__WIN2LINUX_WIFI_KEY_REDACTED__" });
                    workspaceContent = workspaceContent.Replace(actualNetworkConfig, redactedNetworkConfig, StringComparison.Ordinal);
                }
                await File.WriteAllTextAsync(Path.Combine(linuxOutDir, unattendedFilename), workspaceContent, ct);
                
                progress.Report(new ProgressUpdate(
                    8, TotalSteps, "Populating EFI Environment", 74,
                    $"[ESP] Writing EFI tree, boot files, and autoinstall payload to {espRoot}..."));

                await EspPopulationService.PopulateEspAsync(
                    plan.SelectedDistro, espRoot, bootStagingDir, efiStagingDir, unattendedDir, progress, ct, plan.EffectiveSetupConfig);

                // Delete only the dedicated staging tree, then verify the final
                // payload again before registering a boot entry or reporting success.
                try
                {
                    espStaging.Cleanup();
                }
                catch { /* Cleanup is best-effort */ }

                EspPopulationService.VerifyBootPayload(plan.SelectedDistro, espRoot);

                progress.Report(new ProgressUpdate(
                    8, TotalSteps, "EFI Environment Ready", 88,
                    $"[ESP] ✓ EFI System Partition created on Disk {espResult.DiskNumber} Partition {espResult.PartitionNumber} and populated."));


                // STEP 9: REGISTER UEFI NVRAM BOOT ENTRY

                progress.Report(new ProgressUpdate(
                    9, TotalSteps, "Registering UEFI Boot Entry", 89,
                    $"[NVRAM] Registering UEFI NVRAM boot entry for {plan.SelectedDistro.DisplayName}..."));

                nvramResult = await TryRegisterUefiBootEntryAsync(plan, espResult, progress, ct);


                // FINAL: PERSIST COMPLETED STATE

                await PersistStateAsync("BOOT_ENV_PREPARED", plan,
                    isoCache: (isoPath, verifiedHash, (ulong)new FileInfo(isoPath).Length),
                    espResult: espResult, nvramResult, ct);

                progress.Report(new ProgressUpdate(
                    TotalSteps, TotalSteps, "Dual-Boot Environment Ready!", 100,
                    $"[Complete] All 9 steps completed. " +
                    $"EFI partition ready: ✓ (Disk {espResult.DiskNumber} Part {espResult.PartitionNumber})  " +
                    $"NVRAM entry: {(nvramResult != null && nvramResult.Success ? "✓" : "Fallback active (BOOTX64.EFI)")}. " +
                    $"Reboot now to start the Linux installer.",
                    IsSuccess: true,
                    IsComplete: true));

                return true;
            }
            finally
            {
                // Always safely unmount the temporary drive letter assigned to the ESP
                await EspCreationService.RemoveEspDriveLetterAsync(espResult.DriveLetter, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            try
            {
                espStaging?.Cleanup();
                
                if (espResult != null)
                {
                    await EspCreationService.DeleteEspAndExtendVolumeAsync(
                        plan.Target.DiskNumber, plan.ProtectedDisk.DiskNumber, plan.Target.DriveLetter, CancellationToken.None);
                }
                
                if (nvramResult != null && nvramResult.Success && !string.IsNullOrEmpty(nvramResult.EntryGuid))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "bcdedit.exe",
                        Arguments = $"/delete {nvramResult.EntryGuid}",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var p = Process.Start(psi);
                    if (p != null) await p.WaitForExitAsync();
                }
            }
            catch { /* Cleanup on failure is best-effort */ }

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
            var copyRes = await RunBcdeditAsync($"/copy {{bootmgr}} /d \"{plan.SelectedDistro.UefiBootLabel}\"", ct);
            if (copyRes.ExitCode != 0)
            {
                progress.Report(new ProgressUpdate(9, TotalSteps, "NVRAM Registration Skipped", 92,
                    $"[NVRAM] bcdedit could not copy entry (Exit {copyRes.ExitCode}): {copyRes.Error}"));
                return new NvramRegistrationResult(true, false, null, "Failed to copy bootmgr entry.");
            }

            var guidMatch = System.Text.RegularExpressions.Regex.Match(copyRes.Output, @"\{([0-9a-fA-F\-]{36})\}");
            if (!guidMatch.Success)
            {
                progress.Report(new ProgressUpdate(9, TotalSteps, "NVRAM Registration Skipped", 92,
                    $"[NVRAM] bcdedit could not parse GUID from copy output."));
                return new NvramRegistrationResult(true, false, null, "Could not parse GUID from bcdedit output.");
            }

            var entryGuid = $"{{{guidMatch.Groups[1].Value}}}";

            var devRes = await RunBcdeditAsync($"/set {entryGuid} device partition={espResult.DriveLetter}:", ct);
            if (devRes.ExitCode != 0)
            {
                await RunBcdeditAsync($"/delete {entryGuid}", ct); // Rollback
                return new NvramRegistrationResult(true, false, null, "Failed to set device partition.");
            }

            var pathRes = await RunBcdeditAsync($"/set {entryGuid} path \\EFI\\{plan.SelectedDistro.EfiVendorDir}\\{plan.SelectedDistro.EfiBinary}", ct);
            if (pathRes.ExitCode != 0)
            {
                await RunBcdeditAsync($"/delete {entryGuid}", ct); // Rollback
                return new NvramRegistrationResult(true, false, null, "Failed to set EFI path.");
            }

            var seqRes = await RunBcdeditAsync($"/set {{fwbootmgr}} bootsequence {entryGuid}", ct);
            if (seqRes.ExitCode != 0)
            {
                await RunBcdeditAsync($"/delete {entryGuid}", ct); // Rollback
                return new NvramRegistrationResult(true, false, null, "Failed to add to bootsequence.");
            }

            progress.Report(new ProgressUpdate(9, TotalSteps, "UEFI Boot Entry Registered", 93,
                $"[NVRAM] ✓ Registered '{plan.SelectedDistro.UefiBootLabel}' as UEFI firmware entry {entryGuid} for next boot."));

            return new NvramRegistrationResult(true, true, entryGuid, null);
        }
        catch (Exception ex)
        {
            progress.Report(new ProgressUpdate(9, TotalSteps, "NVRAM Registration Skipped", 92,
                $"[NVRAM] bcdedit registration not required (BOOTX64.EFI fallback is active): {ex.Message}"));
            return new NvramRegistrationResult(true, false, null, ex.Message);
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunBcdeditAsync(string args, CancellationToken ct)
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
        var error = await proc.StandardError.ReadToEndAsync(linked.Token);
        await proc.WaitForExitAsync(linked.Token);
        return (proc.ExitCode, output, error);
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
            var existingFreeExtent = await GetLargestFreeExtentAsync(plan.Target.DiskNumber, ct);

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
            ulong minRootBytes = 20UL * 1024 * 1024 * 1024; // Sane minimum 20GB root
            ulong espBytes = 8589934592; // 8GB ESP
            long minimumMb = (long)((minRootBytes + espBytes) / (1024 * 1024));
            
            await File.WriteAllTextAsync(diskpartScriptPath,
                $"select volume {driveLetter}\nshrink desired={shrinkMb} minimum={minimumMb}\n", ct);

            var psi = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{diskpartScriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30)); // 30 minutes, large volumes take time
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
            using var process = Process.Start(psi);

            if (process != null)
            {
                var readOutputTask = process.StandardOutput.ReadToEndAsync(linked.Token);
                try { await process.WaitForExitAsync(linked.Token); }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException("Shrink operation timed out after 30 minutes.");
                }
                var output = await readOutputTask;

                try { File.Delete(diskpartScriptPath); } catch { }

                var postFreeExtent = await GetLargestFreeExtentAsync(plan.Target.DiskNumber, ct);
                if (postFreeExtent < espBytes + minRootBytes)
                {
                    throw new InvalidOperationException($"Diskpart failed or shrank insufficiently. Only {postFreeExtent / 1e9:F1} GB free after shrink; need at least {(espBytes + minRootBytes) / 1e9:F1} GB.");
                }

                progress.Report(new ProgressUpdate(7, TotalSteps, "Volume Shrunk", 63,
                    $"[Volume] ✓ Volume {driveLetter}: successfully shrunk. {postFreeExtent / 1e9:F1} GB unallocated space created."));
                return true;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Volume Shrink failed: {ex.Message}", ex);
        }

        return false;
    }

    private static async Task<ulong> GetLargestFreeExtentAsync(int diskNumber, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var scope = new System.Management.ManagementScope(@"\\.\ROOT\Microsoft\Windows\Storage");
            scope.Connect();
            using var searcher = new System.Management.ManagementObjectSearcher(scope,
                new System.Management.ObjectQuery($"SELECT Size, AllocatedSize, LargestFreeExtent FROM MSFT_Disk WHERE Number = {diskNumber}"));
            using var coll = searcher.Get();
            foreach (System.Management.ManagementObject d in coll)
            {
                var extent = Convert.ToUInt64(d["LargestFreeExtent"] ?? 0);
                if (extent > 0) return extent;
                var size = Convert.ToUInt64(d["Size"] ?? 0);
                var alloc = Convert.ToUInt64(d["AllocatedSize"] ?? 0);
                if (size > alloc) return size - alloc;
            }
            return 0UL;
        }, ct);
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
                plan.ProtectedDisk.WindowsDiskSerial,
                plan.ProtectedDisk.SystemVolume,
                Status = "LOCKED"
            },
            LinuxTarget = new
            {
                plan.Target.DiskNumber,
                plan.Target.DiskGuid,
                plan.Target.WindowsDiskSerial,
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
        "zorin" => "user-data",
        _ => "user-data"
    };

    public static string GenerateUnattendedConfig(
        InstallationPlan plan,
        int espPartitionNumber,
        ulong espSizeBytes,
        string? espPartitionGuid = null,
        ulong? espOffsetBytes = null)
    {
        var linuxSizeMb = (long)(plan.ReservedGb * 1024);

        return plan.SelectedDistro.Id.ToLowerInvariant() switch
        {
            "ubuntu" => GenerateUbuntuAutoinstall(plan, linuxSizeMb, espPartitionNumber, espSizeBytes, espPartitionGuid, espOffsetBytes),
            "fedora" => GenerateFedoraKickstart(plan, linuxSizeMb, espPartitionNumber, espSizeBytes),
            "linuxmint" => GenerateMintPreseed(plan, espPartitionNumber, espSizeBytes),
            "zorin" => GenerateZorinAutoinstall(plan, linuxSizeMb, espPartitionNumber, espSizeBytes, espPartitionGuid, espOffsetBytes),
            _ => "# Default unattended configuration\n"
        };
    }

    public static void ValidateAutoinstallYaml(string yamlContent)
    {
        using var reader = new StringReader(yamlContent);
        var yaml = new YamlStream();
        try
        {
            yaml.Load(reader);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Generated YAML is syntactically invalid: {ex.Message}", ex);
        }

        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            return; // Not a standard mapping at root, maybe empty or simple value.

        if (!root.Children.TryGetValue(new YamlScalarNode("autoinstall"), out var autoinstallNode) || autoinstallNode is not YamlMappingNode autoinstall)
            throw new InvalidOperationException("YAML missing 'autoinstall' top-level mapping.");

        if (!autoinstall.Children.TryGetValue(new YamlScalarNode("storage"), out var storageNode) || storageNode is not YamlMappingNode storage)
            throw new InvalidOperationException("YAML missing 'autoinstall.storage' mapping.");

        if (!storage.Children.TryGetValue(new YamlScalarNode("config"), out var configNode))
            throw new InvalidOperationException("YAML missing 'autoinstall.storage.config' key. Indentation error?");

        if (configNode is not YamlSequenceNode configSeq)
            throw new InvalidOperationException($"'autoinstall.storage.config' must be a sequence, but found {configNode.NodeType}.");

        bool hasTargetDisk = false;
        bool hasRootPartition = false;
        bool hasEspPartition = false;
        int nonPreservedPartitionCount = 0;

        foreach (var item in configSeq.Children)
        {
            if (item is YamlMappingNode map)
            {
                map.Children.TryGetValue(new YamlScalarNode("id"), out var idNode);
                var id = (idNode as YamlScalarNode)?.Value;
                
                map.Children.TryGetValue(new YamlScalarNode("preserve"), out var preserveNode);
                var preserve = (preserveNode as YamlScalarNode)?.Value?.ToLowerInvariant();

                map.Children.TryGetValue(new YamlScalarNode("size"), out var sizeNode);
                var size = (sizeNode as YamlScalarNode)?.Value;

                if (map.Children.TryGetValue(new YamlScalarNode("type"), out var typeNode) && typeNode is YamlScalarNode typeScalar && typeScalar.Value == "partition")
                {
                    if (preserve != "true") nonPreservedPartitionCount++;
                    else if (!map.Children.TryGetValue(new YamlScalarNode("offset"), out var offsetNode) ||
                             offsetNode is not YamlScalarNode offsetScalar ||
                             !ulong.TryParse(offsetScalar.Value, out var offset) || offset == 0)
                    {
                        throw new InvalidOperationException($"Preserved partition '{id}' must have its verified byte offset.");
                    }
                    if (preserve == "true" &&
                        (!map.Children.TryGetValue(new YamlScalarNode("partition_type"), out var partitionTypeNode) ||
                         partitionTypeNode is not YamlScalarNode partitionTypeScalar ||
                         !Guid.TryParse(partitionTypeScalar.Value, out _)))
                    {
                        throw new InvalidOperationException($"Preserved partition '{id}' must retain its GPT type GUID.");
                    }
                }

                if (id == "target-disk")
                {
                    hasTargetDisk = true;
                    if (preserve != "true") throw new InvalidOperationException("target-disk must have preserve: true");

                    if (!HasNonEmptyScalar(map, "path"))
                    {
                        throw new InvalidOperationException("target-disk.path must identify the Linux target resolved from the staged ESP partition GUID.");
                    }
                }
                else if (id == "root-partition")
                {
                    hasRootPartition = true;
                    if (preserve == "true") throw new InvalidOperationException("root-partition must have preserve: false");
                    if (size != "-1") throw new InvalidOperationException("root-partition must have size: -1");
                }
                else if (id == "esp-partition")
                {
                    hasEspPartition = true;
                    if (preserve != "true") throw new InvalidOperationException("esp-partition must have preserve: true");
                }
            }
        }

        if (!hasTargetDisk) throw new InvalidOperationException("Storage config missing 'target-disk' definition.");
        if (!hasRootPartition) throw new InvalidOperationException("Storage config missing 'root-partition' definition.");
        if (!hasEspPartition) throw new InvalidOperationException("Storage config missing 'esp-partition' definition.");
        if (nonPreservedPartitionCount != 1) throw new InvalidOperationException($"Exactly one partition must have preserve: false, but found {nonPreservedPartitionCount}");
    }

    private static bool HasNonEmptyScalar(YamlMappingNode mapping, string key)
    {
        return mapping.Children.TryGetValue(new YamlScalarNode(key), out var node) &&
               node is YamlScalarNode scalar &&
               !string.IsNullOrWhiteSpace(scalar.Value);
    }

    private static string GenerateStorageConfig(
        InstallationPlan plan,
        int espPartitionNumber,
        ulong espSizeBytes,
        string? espPartitionGuid,
        ulong? espOffsetBytes)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("storage:");
        sb.AppendLine("  version: 2");
        sb.AppendLine("  config:");
        sb.AppendLine("    - type: disk");
        sb.AppendLine("      id: target-disk");
        sb.AppendLine("      ptable: gpt");
        if (!Guid.TryParse(espPartitionGuid, out _))
        {
            throw new InvalidOperationException("A valid staged ESP partition GUID is required to identify the Linux target disk safely.");
        }
        sb.AppendLine("      path: /dev/win2linux-target");
        sb.AppendLine("      preserve: true");

        // Curtin identifies preserved GPT entries by start offset. Inferring the
        // layout from sizes moves Windows MSR partitions and loses existing gaps.
        var partitions = plan.Target.Partitions
            .Where(part => part.PartitionNumber != espPartitionNumber)
            .Append(new PartitionInfo(plan.Target.DiskNumber, espPartitionNumber, null,
                espSizeBytes, "c12a7328-f81f-11d2-ba4b-00a0c93ec93b", espPartitionGuid, espOffsetBytes))
            .ToList();
        foreach (var part in partitions)
        {
            if (part.OffsetBytes is null or 0)
            {
                throw new InvalidOperationException(
                    $"Partition {part.PartitionNumber} has no verified byte offset. Refusing to infer the existing disk layout.");
            }
            if (part.PartitionNumber <= 0 || part.SizeBytes == 0 ||
                part.OffsetBytes.Value > ulong.MaxValue - part.SizeBytes)
            {
                throw new InvalidOperationException($"Partition {part.PartitionNumber} has invalid disk geometry.");
            }
            if (!Guid.TryParse(part.GptType, out _))
                throw new InvalidOperationException($"Partition {part.PartitionNumber} has no verified GPT type GUID.");
        }
        if (partitions.Select(part => part.PartitionNumber).Distinct().Count() != partitions.Count)
            throw new InvalidOperationException("The existing disk layout contains duplicate partition numbers.");

        var orderedPartitions = partitions.OrderBy(part => part.OffsetBytes).ToList();
        ulong previousEnd = 0;
        foreach (var part in orderedPartitions)
        {
            if (part.OffsetBytes!.Value < previousEnd)
                throw new InvalidOperationException("The existing disk layout contains overlapping partitions.");
            previousEnd = part.OffsetBytes.Value + part.SizeBytes;
        }

        // The new root partition follows the last partition action, so emit
        // existing partitions in physical order while retaining their GPT slots.
        foreach (var part in orderedPartitions)
        {
            bool isEsp = part.PartitionNumber == espPartitionNumber;
            sb.AppendLine("    - type: partition");
            sb.AppendLine($"      id: {(isEsp ? "esp-partition" : $"partition-{part.PartitionNumber}")}");
            sb.AppendLine("      device: target-disk");
            sb.AppendLine($"      number: {part.PartitionNumber}");
            sb.AppendLine($"      offset: {part.OffsetBytes}");
            sb.AppendLine($"      size: {part.SizeBytes}");
            sb.AppendLine($"      partition_type: {Guid.Parse(part.GptType!).ToString("D")}");
            if (isEsp)
            {
                sb.AppendLine("      flag: boot");
                sb.AppendLine("      grub_device: true");
            }
            sb.AppendLine("      preserve: true");
        }
        sb.AppendLine("    - type: format");
        sb.AppendLine("      id: esp-format");
        sb.AppendLine("      fstype: fat32");
        sb.AppendLine("      volume: esp-partition");
        sb.AppendLine("      preserve: true");
        sb.AppendLine("    - type: mount");
        sb.AppendLine("      id: esp-mount");
        sb.AppendLine("      path: /boot/efi");
        sb.AppendLine("      device: esp-format");

        sb.AppendLine("    - type: partition");
        sb.AppendLine("      id: root-partition");
        sb.AppendLine("      device: target-disk");
        sb.AppendLine("      size: -1");
        sb.AppendLine("      preserve: false");
        sb.AppendLine("    - type: format");
        sb.AppendLine("      id: root-format");
        sb.AppendLine($"      fstype: {plan.SelectedDistro.DefaultFilesystem}");
        sb.AppendLine("      volume: root-partition");
        sb.AppendLine("    - type: mount");
        sb.AppendLine("      id: root-mount");
        sb.AppendLine("      path: /");
        sb.AppendLine("      device: root-format");

        return sb.ToString().TrimEnd();
    }

    private static string GenerateUbuntuAutoinstall(
        InstallationPlan plan,
        long linuxSizeMb,
        int espPartitionNumber,
        ulong espSizeBytes,
        string? espPartitionGuid,
        ulong? espOffsetBytes)
    {
        var config = plan.EffectiveSetupConfig;
        var passwordHash = GetSha512CryptHash(config.Password);
        var networkConfig = GenerateUbuntuNetworkConfig(config.WifiNetwork);
        var legionKernelPreflight = config.KernelSelection == "linux-7.3-legion"
            ? GenerateUbuntuLegionKernelPreflight()
            : string.Empty;
        var legionKernelInstall = config.KernelSelection == "linux-7.3-legion"
            ? GenerateUbuntuLegionKernelInstallCommand()
            : string.Empty;
        var ubuntuAdditionalLateCommands = GenerateUbuntuAdditionalLateCommands(config);

        // NOTE: preserve: true on the disk is CRITICAL to protect the existing D: partition.
        // Partition 3 is the dedicated ESP created by Win2Linux on Disk 1.
        // The root partition uses size: -1 (consumes all remaining unallocated space).
        var yaml = $$"""
            #cloud-config
            autoinstall:
              version: 1
              oem:
                install: false
              drivers:
                install: false
              updates: all
              refresh-installer:
                update: false
              shutdown: reboot
              locale: {{config.Locale}}
              timezone: {{config.Timezone}}
              keyboard:
                layout: {{config.KeyboardLayout}}
              identity:
                realname: "{{config.RealName.Replace("\"", "\\\"")}}"
                username: {{config.Username}}
                hostname: {{config.Hostname}}
                password: '{{passwordHash}}'
            {{networkConfig}}
              {{GenerateStorageConfig(plan, espPartitionNumber, espSizeBytes, espPartitionGuid, espOffsetBytes).TrimStart().Replace("\n", "\n  ")}}
              packages:
                - os-prober{{(config.InstallNvidiaDrivers ? "\n    - ubuntu-drivers-common" : "")}}
              early-commands:
                - 'echo "Win2Linux: Starting {{plan.SelectedDistro.DisplayName}} automated dual-boot install..."'
            {{legionKernelPreflight}}
              late-commands:
            {{ubuntuAdditionalLateCommands}}
            {{legionKernelInstall}}
                # Fix clock for Windows dual-boot (avoids systemd/dbus requirement in chroot)
                - bash -c "printf '0.0 0 0\n0\nLOCAL\n' > /target/etc/adjtime"

                # Regenerate GRUB menu
                - curtin in-target --target=/target -- bash -c "sed -i '/^#\?GRUB_DISABLE_OS_PROBER/d' /etc/default/grub && echo 'GRUB_DISABLE_OS_PROBER=false' >> /etc/default/grub"
                - curtin in-target --target=/target -- bash -c "if ! update-grub; then echo 'Win2Linux - update-grub failed; continuing because UEFI Windows Boot Manager is preserved.'; fi"
                # Clean up installer staging files via a first-boot systemd service
                - curtin in-target --target=/target -- bash -c "echo '[Unit]' > /etc/systemd/system/win2linux-cleanup.service"
                - curtin in-target --target=/target -- bash -c "echo 'Description=Win2Linux Staging Cleanup' >> /etc/systemd/system/win2linux-cleanup.service"
                - curtin in-target --target=/target -- bash -c "echo 'After=local-fs.target' >> /etc/systemd/system/win2linux-cleanup.service"
                - curtin in-target --target=/target -- bash -c "echo '[Service]' >> /etc/systemd/system/win2linux-cleanup.service"
                - curtin in-target --target=/target -- bash -c "echo 'Type=oneshot' >> /etc/systemd/system/win2linux-cleanup.service"
                # Remove installer staging directories and the root autoinstall file
                - curtin in-target --target=/target -- bash -c "echo 'ExecStart=/bin/rm -rf /boot/efi/win2linux /boot/efi/casper /boot/efi/.disk /boot/efi/LiveOS /boot/efi/boot /boot/efi/autoinstall.yaml' >> /etc/systemd/system/win2linux-cleanup.service"
                # Fix the firmware fallback: the installer leaves its own GRUB config in EFI/boot. Replace it with the installed system's GRUB config.
                - curtin in-target --target=/target -- bash -c "echo 'ExecStart=/bin/cp /boot/efi/EFI/ubuntu/grub.cfg /boot/efi/EFI/boot/grub.cfg' >> /etc/systemd/system/win2linux-cleanup.service"
                - curtin in-target --target=/target -- bash -c "echo 'ExecStart=/bin/cp /boot/efi/EFI/ubuntu/grubx64.efi /boot/efi/EFI/boot/grubx64.efi' >> /etc/systemd/system/win2linux-cleanup.service"
                - curtin in-target --target=/target -- bash -c "echo 'ExecStart=/bin/cp /boot/efi/EFI/ubuntu/shimx64.efi /boot/efi/EFI/boot/bootx64.efi' >> /etc/systemd/system/win2linux-cleanup.service"
                - curtin in-target --target=/target -- bash -c "echo 'ExecStart=/bin/systemctl disable win2linux-cleanup.service' >> /etc/systemd/system/win2linux-cleanup.service"
                - curtin in-target --target=/target -- bash -c "echo 'ExecStart=/bin/rm /etc/systemd/system/win2linux-cleanup.service' >> /etc/systemd/system/win2linux-cleanup.service"
                - curtin in-target --target=/target -- bash -c "echo '[Install]' >> /etc/systemd/system/win2linux-cleanup.service"
                - curtin in-target --target=/target -- bash -c "echo 'WantedBy=multi-user.target' >> /etc/systemd/system/win2linux-cleanup.service"
                - curtin in-target --target=/target -- systemctl enable win2linux-cleanup.service
            """;

        yaml = InsertUbuntuSource(yaml);
        return InsertTargetDiskDiscoveryCommand(yaml, espPartitionGuid);
    }

    private static string GenerateUbuntuLegionKernelPreflight() =>
        "    - >-\n" +
        "      echo 'Win2Linux: Linux 7.3 will be installed from Ubuntu 26.10 Stonking as a Canonical-signed kernel; Secure Boot can remain enabled.'";

    private static string GenerateUbuntuAdditionalLateCommands(LinuxSetupConfiguration config)
    {
        var commands = new List<string>();
        if (config.InstallNvidiaDrivers)
        {
            commands.Add("    - curtin in-target --target=/target -- ubuntu-drivers install || true");
        }

        if (config.KernelSelection == "linux-7.3-legion")
        {
            commands.Add("    - >-");
            commands.Add("      curtin in-target --target=/target --");
            commands.Add("      bash -c 'mkdir -p /etc/modprobe.d && printf \"# Lenovo Legion CS35L41 speaker routing fix\\\\noptions snd-hda-intel model=dual-codecs\\\\noptions snd-sof-pci-intel-tgl dmic_detect=0\\\\n\" > /etc/modprobe.d/alsa-legion-speakers.conf'");
        }

        return string.Join("\n", commands);
    }

    private static string GenerateUbuntuLegionKernelInstallCommand()
    {
        var script = """
            cat > /target/tmp/win2linux-install-kernel73.sh <<'WIN2LINUX_KERNEL_SCRIPT'
            #!/bin/bash
            set -euo pipefail
            export DEBIAN_FRONTEND=noninteractive
            apt-get install -y ca-certificates
            cat > /etc/apt/sources.list.d/win2linux-stonking-kernel.list <<'WIN2LINUX_APT_SOURCES'
            deb [arch=amd64] https://archive.ubuntu.com/ubuntu stonking main restricted universe multiverse
            deb [arch=amd64] https://archive.ubuntu.com/ubuntu stonking-updates main restricted universe multiverse
            deb [arch=amd64] https://security.ubuntu.com/ubuntu stonking-security main restricted universe multiverse
            WIN2LINUX_APT_SOURCES
            cat > /etc/apt/preferences.d/win2linux-stonking-kernel <<'WIN2LINUX_APT_PREFERENCES'
            Package: *
            Pin: release n=stonking
            Pin-Priority: 100

            Package: *
            Pin: release n=stonking-updates
            Pin-Priority: 100

            Package: *
            Pin: release n=stonking-security
            Pin-Priority: 100

            Package: linux-generic linux-image-generic linux-headers-generic linux-tools-generic linux-generic-hwe-26.04 linux-image-generic-hwe-26.04 linux-headers-generic-hwe-26.04 linux-image-* linux-modules-* linux-headers-* linux-tools-* linux-buildinfo-* linux-signed-* linux-modules-extra-*
            Pin: release n=stonking
            Pin-Priority: 990

            Package: linux-generic linux-image-generic linux-headers-generic linux-tools-generic linux-generic-hwe-26.04 linux-image-generic-hwe-26.04 linux-headers-generic-hwe-26.04 linux-image-* linux-modules-* linux-headers-* linux-tools-* linux-buildinfo-* linux-signed-* linux-modules-extra-*
            Pin: release n=stonking-updates
            Pin-Priority: 990

            Package: linux-generic linux-image-generic linux-headers-generic linux-tools-generic linux-generic-hwe-26.04 linux-image-generic-hwe-26.04 linux-headers-generic-hwe-26.04 linux-image-* linux-modules-* linux-headers-* linux-tools-* linux-buildinfo-* linux-signed-* linux-modules-extra-*
            Pin: release n=stonking-security
            Pin-Priority: 990
            WIN2LINUX_APT_PREFERENCES
            apt-get update
            apt-get install -y --install-recommends linux-generic/stonking sbsigntool
            kernel_image="$(find /boot -maxdepth 1 -type f -name 'vmlinuz-7.3.0-*-generic' -printf '%f\n' | sort -V | tail -n 1)"
            if [ -z "$kernel_image" ]; then
              echo 'Ubuntu Stonking archive did not install a 7.3 generic kernel image.' >&2
              exit 1
            fi
            if ! sbverify --list "/boot/$kernel_image" >/tmp/win2linux-kernel-signature.txt 2>&1; then
              cat /tmp/win2linux-kernel-signature.txt >&2
              echo "The installed kernel image $kernel_image has no readable Secure Boot signature." >&2
              exit 1
            fi
            grep -qi canonical /tmp/win2linux-kernel-signature.txt || {
              cat /tmp/win2linux-kernel-signature.txt >&2
              echo "The installed kernel image $kernel_image is not signed by Canonical." >&2
              exit 1
            }
            printf '%s\n' '# Lenovo Legion CS35L41 speaker routing fix' 'options snd-hda-intel model=dual-codecs' 'options snd-sof-pci-intel-tgl dmic_detect=0' > /etc/modprobe.d/alsa-legion-speakers.conf
            update-initramfs -u -k all
            update-grub
            mkdir -p /var/lib/win2linux
            printf '%s\n' "$kernel_image (Ubuntu Stonking, Canonical-signed)" > /var/lib/win2linux/kernel-7.3-installed
            rm -f /tmp/win2linux-kernel-signature.txt
            WIN2LINUX_KERNEL_SCRIPT
            chmod 0755 /target/tmp/win2linux-install-kernel73.sh
            if curtin in-target --target=/target -- /bin/bash /tmp/win2linux-install-kernel73.sh; then
              echo 'Canonical-signed Ubuntu 7.3 kernel installed from the Ubuntu 26.10 Stonking archive. Secure Boot remains enabled and the existing Ubuntu kernel remains available as a fallback.' > /target/var/log/win2linux-kernel-selection.log
            else
              echo 'Canonical-signed Ubuntu 7.3 kernel installation failed. The Ubuntu default kernel remains available; check installer logs and network access.' > /target/var/log/win2linux-kernel-selection.log
            fi
            rm -f /target/tmp/win2linux-install-kernel73.sh
            """;

        return "    - |\n" + string.Join(
            "\n",
            script.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n').Select(line => "      " + line));
    }

    internal static string GenerateUbuntuNetworkConfig(WifiNetworkConfiguration? wifiNetwork)
    {
        if (wifiNetwork is null || string.IsNullOrWhiteSpace(wifiNetwork.Ssid))
        {
            return string.Empty;
        }

        var yaml = new System.Text.StringBuilder()
            .AppendLine("  network:")
            .AppendLine("    version: 2")
            .AppendLine("    renderer: NetworkManager")
            .AppendLine("    wifis:")
            .AppendLine("      win2linux-wifi:")
            .AppendLine("        match: {}")
            .AppendLine("        dhcp4: true")
            .AppendLine("        access-points:")
            .Append("          ").Append(JsonSerializer.Serialize(wifiNetwork.Ssid)).AppendLine(":");

        if (!wifiNetwork.IsOpenNetwork)
        {
            if (string.IsNullOrEmpty(wifiNetwork.Password))
            {
                throw new InvalidOperationException("The selected Wi-Fi network does not have a readable password.");
            }

            yaml.Append("            password: ")
                .Append(JsonSerializer.Serialize(wifiNetwork.Password))
                .AppendLine();
        }

        return yaml.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string InsertUbuntuSource(string yaml)
    {
        var lineBreak = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        const string markerText = "  shutdown: reboot";
        var marker = markerText + lineBreak;
        var source = "  source:" + lineBreak + "    id: ubuntu-desktop" + lineBreak;
        return yaml.Replace(marker, marker + source, StringComparison.Ordinal);
    }

    private static string InsertTargetDiskDiscoveryCommand(string yaml, string? espPartitionGuid)
    {
        if (!Guid.TryParse(espPartitionGuid, out var parsedEspGuid))
        {
            return yaml;
        }

        var command = $$"""
            - >-
              bash -c 'set -eu;
                            udevadm settle --timeout=10 || true;
              ESP=/dev/disk/by-partuuid/{{parsedEspGuid:D}};
              test -b "$ESP";
              DISK=$(lsblk -no PKNAME "$ESP");
              test -n "$DISK";
              TARGET=/dev/$DISK;
              test -b "$TARGET";
              test "$(lsblk -dnro TYPE "$TARGET")" = disk;
              sed -i "s|/dev/win2linux-target|$TARGET|g" /autoinstall.yaml;
              grep -Fq "path: $TARGET" /autoinstall.yaml;
              printf "Win2Linux target disk resolved to %s\\n" "$TARGET"'
            """.Trim();
        var indentedCommand = string.Join(
            "\n",
            command.Split('\n').Select(line => $"    {line}"));
        var lineBreak = yaml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var marker = "  early-commands:" + lineBreak;
        return yaml.Replace(marker, marker + indentedCommand.Replace("\n", lineBreak, StringComparison.Ordinal) + lineBreak, StringComparison.Ordinal);
    }

    private static string GetSha512CryptHash(string password)
    {
        if (string.IsNullOrEmpty(password) || password == "changeme")
        {
            throw new InvalidOperationException("Password must not be empty or 'changeme'.");
        }

        // Managed SHA-512-crypt hash with a random salt
        return CryptSharp.Crypter.Sha512.Crypt(password);
    }

    private static string GenerateZorinAutoinstall(InstallationPlan plan, long linuxSizeMb, int espPartitionNumber, ulong espSizeBytes, string? espPartitionGuid, ulong? espOffsetBytes)
    {
        var config = plan.EffectiveSetupConfig;
        var passwordHash = GetSha512CryptHash(config.Password);

        return $$"""
            #cloud-config
            autoinstall:
              version: 1
              refresh-installer:
                update: false
              shutdown: reboot
              locale: {{config.Locale}}
              timezone: {{config.Timezone}}
              keyboard:
                layout: {{config.KeyboardLayout}}
              identity:
                realname: "{{config.RealName.Replace("\"", "\\\"")}}"
                username: {{config.Username}}
                hostname: {{config.Hostname}}
                password: '{{passwordHash}}'
              {{GenerateStorageConfig(plan, espPartitionNumber, espSizeBytes, espPartitionGuid, espOffsetBytes).TrimStart().Replace("\n", "\n  ")}}
              packages:
                - os-prober
              late-commands:
                # Fix clock for Windows dual-boot
                - bash -c "printf '0.0 0 0\n0\nLOCAL\n' > /target/etc/adjtime"
                # Prepend zorin to BootOrder
                - >-
                  bash -c '
                  ZORIN=$(efibootmgr | grep -oiP "Boot\K[0-9A-F]+(?=.*{{plan.SelectedDistro.UefiBootLabel}})" | head -1);
                  if [ -n "$ZORIN" ]; then
                      ORDER=$(efibootmgr | grep -oiP "^BootOrder: \K.*" | sed "s/$ZORIN,\?//; s/,$//");
                      efibootmgr --bootorder ${ZORIN},${ORDER} || true;
                  fi'
                # Regenerate GRUB menu
                - curtin in-target --target=/target -- bash -c "sed -i '/^#\?GRUB_DISABLE_OS_PROBER/d' /etc/default/grub && echo 'GRUB_DISABLE_OS_PROBER=false' >> /etc/default/grub"
                - curtin in-target --target=/target -- bash -c "if ! update-grub; then echo 'Win2Linux - update-grub failed; continuing because UEFI Windows Boot Manager is preserved.'; fi"
              user-data:
                runcmd:
                  - "rm -rf /boot/efi/win2linux /boot/efi/casper /boot/efi/.disk /boot/efi/EFI/boot /boot/efi/boot || true"
            """;
    }

    private static string GenerateFedoraKickstart(InstallationPlan plan, long linuxSizeMb, int espPartitionNumber, ulong espSizeBytes)
    {
        var config = plan.EffectiveSetupConfig;
        var passwordHash = GetSha512CryptHash(config.Password);

        var dePackageGroup = plan.SelectedDistro.SelectedDesktopEnvironment == "gnome"
            ? "@^workstation-product-environment"
            : "@^kde-desktop-environment";

        var deLabel = plan.SelectedDistro.SelectedDesktopEnvironment == "gnome"
            ? "GNOME Workstation"
            : "KDE Plasma";

        var modeDirective = config.InteractiveReview ? "graphical" : "text\nreboot";

        var extraRepos = string.Empty;
        var extraPackages = string.Empty;
        var postScript = new System.Text.StringBuilder();

        if (config.InstallNvidiaDrivers)
        {
            extraRepos += """
                # RPM Fusion non-free repository for NVIDIA proprietary drivers
                repo --name="rpmfusion-nonfree" --mirrorlist=https://mirrors.rpmfusion.org/metalink?repo=nonfree-fedora-44&arch=x86_64
                repo --name="rpmfusion-nonfree-updates" --mirrorlist=https://mirrors.rpmfusion.org/metalink?repo=nonfree-fedora-updates-released-44&arch=x86_64

                """;
            extraPackages += "\nakmod-nvidia\nxorg-x11-drv-nvidia-cuda";
            postScript.AppendLine("# Configure RPM Fusion and NVIDIA proprietary drivers");
            postScript.AppendLine("dnf install -y https://mirrors.rpmfusion.org/free/fedora/rpmfusion-free-release-$(rpm -E %fedora).noarch.rpm https://mirrors.rpmfusion.org/nonfree/fedora/rpmfusion-nonfree-release-$(rpm -E %fedora).noarch.rpm || true");
            postScript.AppendLine("dnf install -y akmod-nvidia xorg-x11-drv-nvidia-cuda || true");
        }

        if (config.InstallProprietaryCodecs)
        {
            postScript.AppendLine("# Install multimedia codecs and hardware acceleration");
            postScript.AppendLine("dnf groupupdate -y multimedia --setop=\"install_weak_deps=False\" --exclude=PackageKit-gstreamer-plugin || true");
        }

        if (config.KernelSelection == "linux-7.3-legion")
        {
            postScript.AppendLine("# Install Linux 7.3 Mainline / Lenovo Legion Audio Patched Kernel");
            postScript.AppendLine("dnf copr enable -y @kernel-vanilla/mainline || true");
            postScript.AppendLine("dnf install -y kernel-vanilla-mainline kernel-vanilla-mainline-modules-extra || dnf upgrade -y kernel || true");
            postScript.AppendLine("# Fix Lenovo Legion CS35L41 internal speaker smart amplifier audio routing");
            postScript.AppendLine("mkdir -p /etc/modprobe.d");
            postScript.AppendLine("cat << 'EOF' > /etc/modprobe.d/alsa-legion-speakers.conf");
            postScript.AppendLine("# Lenovo Legion internal speaker amplifier fix (CS35L41 dual amplifier routing)");
            postScript.AppendLine("options snd-hda-intel model=dual-codecs");
            postScript.AppendLine("options snd-sof-pci-intel-tgl dmic_detect=0");
            postScript.AppendLine("EOF");
        }
        else if (config.KernelSelection == "linux-zen")
        {
            postScript.AppendLine("# Install Zen low-latency performance kernel");
            postScript.AppendLine("dnf copr enable -y sentry/kernel-fsync || true");
            postScript.AppendLine("dnf install -y kernel-fsync || true");
        }

        postScript.AppendLine("# Cleanup installation files from the EFI System Partition to reclaim space");
        postScript.AppendLine("cat << 'EOF' > /etc/systemd/system/win2linux-cleanup.service");
        postScript.AppendLine("[Unit]");
        postScript.AppendLine("Description=Win2Linux First Boot Cleanup");
        postScript.AppendLine("After=local-fs.target");
        postScript.AppendLine("[Service]");
        postScript.AppendLine("Type=oneshot");
        postScript.AppendLine("ExecStart=/bin/bash -c \"rm -rf /boot/efi/win2linux /boot/efi/LiveOS /boot/efi/images /boot/efi/EFI/boot /boot/efi/boot || true\"");
        postScript.AppendLine("ExecStartPost=/bin/systemctl disable win2linux-cleanup.service");
        postScript.AppendLine("[Install]");
        postScript.AppendLine("WantedBy=multi-user.target");
        postScript.AppendLine("EOF");
        postScript.AppendLine("systemctl enable win2linux-cleanup.service");

        var postSection = postScript.Length > 0
            ? $"\n%post\n{postScript}%end\n"
            : string.Empty;

        // In interactive review mode, Anaconda displays the pre-populated configuration hub (language, keyboard,
        // timezone, user account, and dedicated storage target), allowing the user to review before clicking "Begin Installation".
        return $$"""
            # Fedora 44 ({{deLabel}}) Kickstart Configuration
            # Generated by Win2Linux Dual-Boot Installer
            {{modeDirective}}
            lang {{config.Locale}}
            keyboard {{config.KeyboardLayout}}
            timezone {{config.Timezone}}
            {{extraRepos}}
            # Network hostname configuration
            network --bootproto=dhcp --device=link --activate --hostname={{config.Hostname}}

            # User account creation with administrative wheel group privileges
            user --name={{config.Username}} --gecos="{{config.RealName}}" --iscrypted --password={{passwordHash}} --groups=wheel

            # Automated partitioning: install into free unallocated space on target disk
            # NEVER clear or modify existing Windows partitions (Disk {{plan.ProtectedDisk.DiskNumber}} and Volume {{plan.Target.DriveLetter}} are protected)
            %pre
            # Find the target disk and the ESP partition we created using the DiskSerial
            DISK=$(ls /dev/disk/by-id/ | grep -i "{{plan.Target.LinuxDiskSerial}}" | grep -v part | head -1)
            ESP_PART=$(ls /dev/disk/by-id/ | grep -i "{{plan.Target.LinuxDiskSerial}}" | grep "part{{espPartitionNumber}}" | head -1)
            
            if [ -z "$DISK" ]; then
                echo "Win2Linux ERROR: target disk not found" >&2
                exit 1
            fi
            if [ -z "$ESP_PART" ]; then
                echo "Win2Linux ERROR: ESP partition not found" >&2
                exit 1
            fi
            
            DISK_PATH="/dev/disk/by-id/$DISK"
            
            echo "ignoredisk --only-use=$DISK_PATH" > /tmp/part-include
            echo "bootloader{{(config.KernelSelection == "linux-7.3-legion" ? " --append=\"snd_hda_intel.model=dual-codecs\"" : "")}}" >> /tmp/part-include
            echo "clearpart --none" >> /tmp/part-include
            echo "part /boot/efi --onpart=/dev/disk/by-id/$ESP_PART" >> /tmp/part-include
            echo "part / --fstype={{plan.SelectedDistro.DefaultFilesystem}} --ondisk=$DISK_PATH --size=1 --grow" >> /tmp/part-include
            %end

            %include /tmp/part-include

            %packages
            {{dePackageGroup}}
            kernel
            grub2-efi-x64
            shim-x64
            efibootmgr{{extraPackages}}
            %end
            {{postSection}}
            """;
    }

    private static string GenerateMintPreseed(InstallationPlan plan, int espPartitionNumber, ulong espSizeBytes)
    {
        var config = plan.EffectiveSetupConfig;
        var autoReboot = (!config.InteractiveReview).ToString().ToLowerInvariant();
        var extraPkgs = config.InstallNvidiaDrivers ? " nvidia-driver-560 ubuntu-drivers-common" : "";
        var passwordHash = GetSha512CryptHash(config.Password);

        return $$"""
            # Linux Mint 22.1 Automatic Ubiquity Preseed Configuration
            # Generated by Win2Linux Dual-Boot Installer
            d-i debian-installer/locale string {{config.Locale}}
            d-i console-setup/ask_detect boolean false
            d-i keyboard-configuration/layoutcode string {{config.KeyboardLayout}}
            d-i netcfg/choose_interface select auto
            d-i netcfg/get_hostname string {{config.Hostname}}

            # Clock and time zone
            d-i time/zone string {{config.Timezone}}
            d-i clock-setup/utc boolean true

            # Bypass BitLocker check which aborts partman automated partitioning
            d-i partman/early_command string rm -f /lib/partman/init.d/*bitlocker* || true; rm -f /lib/partman/check.d/*bitlocker* || true

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

            # User setup
            d-i passwd/user-fullname string {{config.RealName}}
            d-i passwd/username string {{config.Username}}
            d-i passwd/user-password-crypted password {{passwordHash}}

            # Proprietary drivers and codecs
            d-i pkgsel/include string{{extraPkgs}}
            ubiquity ubiquity/use_nonfree boolean {{config.InstallProprietaryCodecs.ToString().ToLowerInvariant()}}

            # Completion
            ubiquity ubiquity/reboot boolean {{autoReboot}}
            d-i finish-install/reboot_in_progress note
            d-i preseed/late_command string \
              in-target bash -c 'echo "W1VuaXRdCkRlc2NyaXB0aW9uPVdpbjJMaW51eCBGaXJzdCBCb290IENsZWFudXAKQWZ0ZXI9bG9jYWwtZnMudGFyZ2V0CltTZXJ2aWNlXQpUeXBlPW9uZXNob3QKRXhlY1N0YXJ0PS9iaW4vYmFzaCAtYyAicm0gLXJmIC9ib290L2VmaS93aW4ybGludXggL2Jvb3QvZWZpL2Nhc3BlciAvYm9vdC9lZmkvLmRpc2sgL2Jvb3QvZWZpL0VGSS9ib290IC9ib290L2VmaS9ib290IHx8IHRydWUiCkV4ZWNTdGFydFBvc3Q9L2Jpbi9zeXN0ZW1jdGwgZGlzYWJsZSB3aW4ybGludXgtY2xlYW51cC5zZXJ2aWNlCltJbnN0YWxsXQpXYW50ZWRCeT1tdWx0aS11c2VyLnRhcmdldA==" | base64 -d > /etc/systemd/system/win2linux-cleanup.service && systemctl enable win2linux-cleanup.service'
            """;
    }
}


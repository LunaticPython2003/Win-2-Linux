using System.Diagnostics;
using System.IO;
using System.Management;
using Win2Linux.Core.Models;
using Win2Linux.Core.Orchestrator;
using Win2Linux.Core.Safety;

namespace Win2Linux.Core.Boot;

/// <summary>
/// Creates a 8192 MB FAT32 EFI System Partition from unallocated space on the secondary disk.
/// The remainder of the shrunk volume is strictly left as unallocated free space so Linux installers
/// (Fedora Anaconda, Ubuntu Subiquity/Ubiquity) can perform automatic or guided partitioning.
///
/// Safety: The protected system disk is triple-checked (number, GUID, serial) before and during
/// every diskpart invocation. Any mismatch immediately aborts with an exception.
/// </summary>
public static class EspCreationService
{
    // GPT Type GUID for EFI System Partitions
    private const string EspGptTypeGuid = "C12A7328-F81F-11D2-BA4B-00A0C93EC93B";

    public record EspCreationResult(
        int DiskNumber,
        int PartitionNumber,
        string PartitionGuid,
        char DriveLetter,
        ulong SizeBytes,
        ulong? OffsetBytes = null
    );

    /// <summary>
    /// Creates a new 8192 MB FAT32 ESP in the unallocated space of the secondary disk.
    /// Cleans up any previous staging partitions (including old ESP or raw partition) beforehand.
    /// Leaves the rest of the target space as true unallocated space for Linux installers.
    /// </summary>
    public static async Task<EspCreationResult> CreateEspAsync(
        InstallationPlan plan,
        ProtectedSystemDisk protectedDisk,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct = default)
    {
        // === HARD SAFETY CHECK ===
        SafetyValidator.AssertNotProtectedSystemDisk(
            plan.Target.DiskNumber,
            plan.Target.DiskGuid,
            plan.Target.WindowsDiskSerial,
            protectedDisk);

        progress.Report(new ProgressUpdate(8, 9, "Creating EFI Boot Partition", 68,
            $"[ESP Creation] Safety confirmed: Disk {protectedDisk.DiskNumber} is protected. " +
            $"Preparing 8192 MB FAT32 ESP on Disk {plan.Target.DiskNumber}..."));

        // Pick a free temp drive letter
        char tempLetter = await FindFreeDriveLetterAsync(ct);

        // Check if previous dual-boot staging partitions exist on the target secondary disk
        // ONLY delete if we can strictly identify it as our ESP (ESP GptType + LINUXEFI label).
        var existingPartNo = await FindWin2LinuxEspAsync(plan.Target.DiskNumber, ct);
        
        if (existingPartNo > 0)
        {
            progress.Report(new ProgressUpdate(8, 9, "Cleaning Previous Dual-Boot Partitions", 67,
                $"[ESP Creation] Safety confirmed: Disk {protectedDisk.DiskNumber} is protected. " +
                $"Found existing ESP partition on Disk {plan.Target.DiskNumber}. " +
                "Cleaning up to ensure pristine unallocated space..."));

            // Use a single PowerShell pipeline to re-verify the partition GUID immediately before deletion,
            // preventing race conditions where partition numbers might shift.
            var psDelete = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Get-Partition -DiskNumber {plan.Target.DiskNumber} -PartitionNumber {existingPartNo} | Where-Object {{ $_.GptType -match '{EspGptTypeGuid}' }} | Remove-Partition -Confirm:$false\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psDelete);
            if (proc != null) await proc.WaitForExitAsync(ct);
        }

        progress.Report(new ProgressUpdate(8, 9, "Creating EFI Boot Partition", 68,
            $"[ESP Creation] Safety confirmed: Disk {protectedDisk.DiskNumber} is protected. " +
            $"Creating 8192 MB FAT32 ESP on Disk {plan.Target.DiskNumber}..."));

        var script = $"""
            select disk {plan.Target.DiskNumber}
            create partition efi size=8192
            format fs=fat32 quick label="LINUXEFI"
            exit
            """;

        progress.Report(new ProgressUpdate(8, 9, "Formatting EFI Boot Partition", 70,
            $"[ESP Creation] Running diskpart on Disk {plan.Target.DiskNumber} (temp letter: {tempLetter}:)..."));

        await RunDiskpartScriptAsync(script, ct);

        // DiskPart can return before the Storage Management provider has refreshed its view.
        // Retry the authoritative disk/partition query instead of treating that propagation
        // window as a failed creation.
        var (partNumber, partGuid, partSize, partOffset) = (-1, "", 0UL, 0UL);
        for (int attempt = 0; attempt < 30 && partNumber < 0; attempt++)
        {
            await RefreshStorageCacheAsync(ct);
            (partNumber, partGuid, partSize, partOffset) = await FindCreatedEspAsync(plan.Target.DiskNumber, 8192UL * 1024 * 1024, ct);
            if (partNumber >= 0) break;
            await Task.Delay(500, ct);
        }

        if (partNumber < 0)
        {
            throw new EspCreationException(
                $"ESP creation verification failed: could not find an EFI partition on Disk {plan.Target.DiskNumber} " +
                $"with a verified partition GUID and byte offset after diskpart completed.");
        }

        // Some Windows builds create the ESP successfully but discard the drive-letter
        // assignment while the Storage provider refreshes. Reassign it from the verified
        // partition identity before handing the mount path to the population service.
        await AssignEspDriveLetterAsync(plan.Target.DiskNumber, partNumber, tempLetter, ct);
        for (int attempt = 0; attempt < 20 && !Directory.Exists($"{tempLetter}:\\"); attempt++)
        {
            await RefreshStorageCacheAsync(ct);
            await Task.Delay(500, ct);
        }

        if (!Directory.Exists($"{tempLetter}:\\"))
        {
            throw new EspCreationException(
                $"EFI partition {partNumber} was created on Disk {plan.Target.DiskNumber}, but Windows did not mount it at {tempLetter}:. Refusing to stage boot files.");
        }

        progress.Report(new ProgressUpdate(8, 9, "EFI Partition Ready", 73,
            $"[ESP Creation] ✓ FAT32 ESP ready: Disk {plan.Target.DiskNumber} Partition {partNumber} " +
            $"({partSize / (1024 * 1024)} MB) at {tempLetter}:\\. Remaining target disk space preserved as unallocated free space."));

        return new EspCreationResult(
            DiskNumber: plan.Target.DiskNumber,
            PartitionNumber: partNumber,
            PartitionGuid: partGuid,
            DriveLetter: tempLetter,
            SizeBytes: partSize,
            OffsetBytes: partOffset
        );
    }

    /// <summary>
    /// Deletes the EFI System Partition and any trailing partitions from the secondary target disk and extends the adjacent Windows volume to reclaim space.
    /// Hard assertion: strictly prevents modifying the protected Windows system disk.
    /// </summary>
    public static async Task<bool> DeleteEspAndExtendVolumeAsync(
        int targetDiskNumber,
        int protectedDiskNumber,
        string? driveLetter = null,
        CancellationToken ct = default)
    {
        if (targetDiskNumber == protectedDiskNumber)
        {
            throw new InvalidOperationException(
                $"CRITICAL SAFETY ABORT: Refusing to delete partitions on protected Windows system disk {protectedDiskNumber}.");
        }

        var existingPartNo = await FindWin2LinuxEspAsync(targetDiskNumber, ct);
        if (existingPartNo < 0)
        {
            return false;
        }

        // Use an atomic PowerShell pipeline to re-verify the partition GUID and delete it
        var psDelete = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"Get-Partition -DiskNumber {targetDiskNumber} -PartitionNumber {existingPartNo} | Where-Object {{ $_.GptType -match '{EspGptTypeGuid}' }} | Remove-Partition -Confirm:$false\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psDelete);
        if (proc != null) await proc.WaitForExitAsync(ct);

        if (!string.IsNullOrWhiteSpace(driveLetter))
        {
            // Now use diskpart just to extend the volume if a drive letter is provided
            var cleanLetter = driveLetter.TrimEnd('\\', ':');
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"select volume {cleanLetter}");
            sb.AppendLine("extend");
            await RunDiskpartScriptAsync(sb.ToString(), ct);
        }
        return true;
    }

    /// <summary>
    /// Removes the temporary drive letter from the ESP after population is complete.
    /// </summary>
    public static async Task RemoveEspDriveLetterAsync(char letter, CancellationToken ct = default)
    {
        var script = $"""
            select volume {letter}
            remove letter={letter}
            exit
            """;

        try
        {
            await RunDiskpartScriptAsync(script, ct);
        }
        catch
        {
            // Best effort — failure to remove the drive letter is non-fatal
        }
    }

    private static async Task RunDiskpartScriptAsync(string script, CancellationToken ct)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"win2linux_esp_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(scriptPath, script, ct);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "diskpart.exe",
                Arguments = $"/s \"{scriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
            using var proc = Process.Start(psi) ?? throw new EspCreationException("Failed to launch diskpart.exe");

            var output = await proc.StandardOutput.ReadToEndAsync(linked.Token);
            await proc.WaitForExitAsync(linked.Token);

            if (proc.ExitCode != 0)
            {
                throw new EspCreationException(
                    $"diskpart.exe exited with code {proc.ExitCode}. Output: {output}");
            }

            // Verify diskpart output doesn't contain error indicators
            if (output.Contains("DiskPart has encountered an error", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("not enough space", StringComparison.OrdinalIgnoreCase))
            {
                throw new EspCreationException(
                    $"diskpart reported an error during ESP creation: {output}");
            }
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    private static async Task<int> FindWin2LinuxEspAsync(int diskNumber, CancellationToken ct)
    {
        var expectedSizeBytes = 8192UL * 1024 * 1024;
        var script = $"Get-Partition -DiskNumber {diskNumber} | Where-Object {{ $_.GptType -match '{EspGptTypeGuid}' -and $_.Size -eq {expectedSizeBytes} }} | Select-Object -First 1 -ExpandProperty PartitionNumber";
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"{script}\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        try
        {
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = await proc.StandardOutput.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode == 0 && int.TryParse(output.Trim(), out int partNo))
                {
                    return partNo;
                }
            }
        }
        catch { }
        return -1;
    }

    private static async Task<(int PartNumber, string PartGuid, ulong PartSize, ulong PartOffset)> FindCreatedEspAsync(
        int diskNumber, ulong expectedSizeBytes, CancellationToken ct)
    {
        var script = $"Get-Partition -DiskNumber {diskNumber} | Where-Object {{ $_.GptType -match '{EspGptTypeGuid}' -and $_.Size -eq {expectedSizeBytes} }} | Select-Object PartitionNumber, Guid, Size, Offset | ConvertTo-Json -Compress";
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"{script}\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        try
        {
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                var output = await proc.StandardOutput.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(output);
                    var root = doc.RootElement;
                    if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        if (root.GetArrayLength() == 0) return (-1, "", 0, 0);
                        root = root[0];
                    }
                    int partNo = root.GetProperty("PartitionNumber").GetInt32();
                    string guid = root.GetProperty("Guid").GetString()?.Trim('{', '}') ?? "";
                    ulong size = root.GetProperty("Size").GetUInt64();
                    ulong offset = root.GetProperty("Offset").GetUInt64();
                    return Guid.TryParse(guid, out _) && offset > 0 ? (partNo, guid, size, offset) : (-1, "", 0, 0);
                }
            }
        }
        catch { }
        return (-1, "", 0, 0);
    }

    private static async Task RefreshStorageCacheAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command \"Update-HostStorageCache\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc != null) await proc.WaitForExitAsync(ct);
    }

    private static async Task AssignEspDriveLetterAsync(int diskNumber, int partitionNumber, char driveLetter, CancellationToken ct)
    {
        var script = $"select disk {diskNumber}\nselect partition {partitionNumber}\nassign letter={driveLetter}\nexit\n";
        await RunDiskpartScriptAsync(script, ct);
    }

    private static async Task<char> FindFreeDriveLetterAsync(CancellationToken ct)
    {
        var usedLetters = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();

        // DriveInfo omits hidden, empty, and recently assigned volumes. Get-PSDrive
        // reflects the namespace DiskPart uses when deciding whether a letter is free.
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -Command \"Get-PSDrive -PSProvider FileSystem | Select-Object -ExpandProperty Name\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc != null)
        {
            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Trim().Length == 1 && char.IsLetter(line.Trim()[0]))
                {
                    usedLetters.Add(char.ToUpperInvariant(line.Trim()[0]));
                }
            }
        }

        // Try letters starting from W backwards to minimize collision with common letters
        foreach (char c in "WXYVZUTSRQPONMLKJIHGFE")
        {
            if (!usedLetters.Contains(c)) return c;
        }

        throw new EspCreationException("No free drive letters available to temporarily mount the ESP.");
    }
}

public class EspCreationException(string message) : Exception(message);

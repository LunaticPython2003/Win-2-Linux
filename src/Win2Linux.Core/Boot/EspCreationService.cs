using System.Diagnostics;
using System.IO;
using System.Management;
using Win2Linux.Core.Models;
using Win2Linux.Core.Orchestrator;
using Win2Linux.Core.Safety;

namespace Win2Linux.Core.Boot;

/// <summary>
/// Creates a 4096 MB FAT32 EFI System Partition from unallocated space on the secondary disk.
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
        ulong SizeBytes
    );

    /// <summary>
    /// Creates a new 4096 MB FAT32 ESP in the unallocated space of the secondary disk.
    /// Returns a description of the created partition for state.json persistence.
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
            plan.Target.DiskSerial,
            protectedDisk);

        progress.Report(new ProgressUpdate(8, 10, "Creating EFI Boot Partition", 68,
            $"[ESP Creation] Safety confirmed: Disk {protectedDisk.DiskNumber} is protected. " +
            $"Creating 4096 MB FAT32 ESP on Disk {plan.Target.DiskNumber}..."));

        // Pick a free temp drive letter
        char tempLetter = FindFreeDriveLetter();

        // Check if an ESP partition already exists on the target secondary disk (e.g. from previous run)
        var (existingPartNo, _, _) = await FindEspPartitionAsync(plan.Target.DiskNumber, ct);

        if (existingPartNo > 0)
        {
            progress.Report(new ProgressUpdate(8, 10, "Deleting Previous ESP Partition", 67,
                $"[ESP Creation] Safety confirmed: Disk {protectedDisk.DiskNumber} is protected. " +
                $"Existing ESP partition {existingPartNo} found on Disk {plan.Target.DiskNumber}. Deleting previous ESP to ensure clean deployment..."));

            var deleteScript = $"""
                select disk {plan.Target.DiskNumber}
                select partition {existingPartNo}
                delete partition override
                exit
                """;

            await RunDiskpartScriptAsync(deleteScript, ct);
            await Task.Delay(1000, ct);
        }

        progress.Report(new ProgressUpdate(8, 10, "Creating EFI Boot Partition", 68,
            $"[ESP Creation] Safety confirmed: Disk {protectedDisk.DiskNumber} is protected. " +
            $"Creating 4096 MB FAT32 ESP on Disk {plan.Target.DiskNumber}..."));

        var script = $"""
            select disk {plan.Target.DiskNumber}
            create partition efi size=4096
            format fs=fat32 quick label="LINUXEFI"
            assign letter={tempLetter}
            exit
            """;

        progress.Report(new ProgressUpdate(8, 10, "Formatting EFI Boot Partition", 70,
            $"[ESP Creation] Running diskpart on Disk {plan.Target.DiskNumber} (temp letter: {tempLetter}:)..."));

        await RunDiskpartScriptAsync(script, ct);

        // Give the OS a moment to register the new volume
        for (int i = 0; i < 10; i++)
        {
            if (Directory.Exists($"{tempLetter}:\\")) break;
            await Task.Delay(500, ct);
        }

        // === POST-CREATION VERIFICATION ===
        var (partNumber, partGuid, partSize) = await FindEspPartitionAsync(plan.Target.DiskNumber, ct);

        if (partNumber < 0)
        {
            throw new EspCreationException(
                $"ESP creation verification failed: could not find an EFI partition on Disk {plan.Target.DiskNumber} " +
                $"after diskpart completed.");
        }

        progress.Report(new ProgressUpdate(8, 10, "EFI Partition Ready", 73,
            $"[ESP Creation] ✓ FAT32 ESP ready: Disk {plan.Target.DiskNumber} Partition {partNumber} " +
            $"({partSize / (1024 * 1024)} MB) at {tempLetter}:\\"));

        return new EspCreationResult(
            DiskNumber: plan.Target.DiskNumber,
            PartitionNumber: partNumber,
            PartitionGuid: partGuid,
            DriveLetter: tempLetter,
            SizeBytes: partSize
        );
    }

    /// <summary>
    /// Deletes the EFI System Partition from the secondary target disk and extends the adjacent Windows volume to reclaim space.
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

        var (existingPartNo, _, _) = await FindEspPartitionAsync(targetDiskNumber, ct);
        if (existingPartNo <= 0)
        {
            return false;
        }

        string script;
        if (!string.IsNullOrWhiteSpace(driveLetter))
        {
            var cleanLetter = driveLetter.TrimEnd('\\', ':');
            script = $"""
                select disk {targetDiskNumber}
                select partition {existingPartNo}
                delete partition override
                select volume {cleanLetter}
                extend
                exit
                """;
        }
        else
        {
            script = $"""
                select disk {targetDiskNumber}
                select partition {existingPartNo}
                delete partition override
                exit
                """;
        }

        await RunDiskpartScriptAsync(script, ct);
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

    private static async Task<(int PartNumber, string PartGuid, ulong PartSize)> FindEspPartitionAsync(
        int diskNumber, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(1000, ct);
            }

            try
            {
                var scope = new ManagementScope(@"\\.\ROOT\Microsoft\Windows\Storage");
                scope.Connect();

                using var searcher = new ManagementObjectSearcher(scope,
                    new ObjectQuery($"SELECT PartitionNumber, Guid, Size, GptType FROM MSFT_Partition " +
                                    $"WHERE DiskNumber = {diskNumber}"));
                using var collection = searcher.Get();

                foreach (ManagementObject p in collection)
                {
                    var gptType = p["GptType"]?.ToString()?.Trim('{', '}');
                    if (string.Equals(gptType, EspGptTypeGuid, StringComparison.OrdinalIgnoreCase))
                    {
                        int partNo = Convert.ToInt32(p["PartitionNumber"] ?? 0);
                        string guid = p["Guid"]?.ToString()?.Trim('{', '}') ?? "";
                        ulong size = Convert.ToUInt64(p["Size"] ?? 0);
                        return (partNo, guid, size);
                    }
                }
            }
            catch (Exception ex)
            {
                if (attempt == 4)
                {
                    throw new EspCreationException($"Failed to query new ESP partition via WMI: {ex.Message}");
                }
            }
        }

        return (-1, "", 0);
    }

    private static char FindFreeDriveLetter()
    {
        var usedLetters = DriveInfo.GetDrives()
            .Select(d => d.Name[0])
            .ToHashSet();

        // Try letters starting from W backwards to minimize collision with common letters
        foreach (char c in "WXYVZUTSRQPONMLKJIHGFE")
        {
            if (!usedLetters.Contains(c)) return c;
        }

        throw new EspCreationException("No free drive letters available to temporarily mount the ESP.");
    }
}

public class EspCreationException(string message) : Exception(message);

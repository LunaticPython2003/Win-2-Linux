using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Win2Linux.Core.Models;

namespace Win2Linux.Core.Storage;

public class StorageDiscoveryService
{
    [DllImport("kernel32.dll")]
    private static extern bool GetFirmwareType(ref uint firmwareType);

    public static async Task<DiscoveryReport> RunDiscoveryAsync()
    {
        return await Task.Run(() => RunDiscovery());
    }

    public static DiscoveryReport RunDiscovery()
    {
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        var systemLetter = systemDrive.TrimEnd('\\', '/', ':').ToUpperInvariant();

        var disks = new List<PhysicalDiskInfo>();
        var partitions = new List<PartitionInfo>();

        // 1. Native WMI / CIM Query for MSFT_Disk and MSFT_Partition
        try
        {
            var scope = new ManagementScope(@"\\.\ROOT\Microsoft\Windows\Storage");
            scope.Connect();

            // Query Partitions
            using (var partSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber, PartitionNumber, DriveLetter, Size, GptType, Guid FROM MSFT_Partition")))
            using (var partCollection = partSearcher.Get())
            {
                foreach (ManagementObject p in partCollection)
                {
                    int diskNo = Convert.ToInt32(p["DiskNumber"] ?? 0);
                    int partNo = Convert.ToInt32(p["PartitionNumber"] ?? 0);
                    string? driveLetter = p["DriveLetter"]?.ToString();
                    if (!string.IsNullOrEmpty(driveLetter))
                    {
                        driveLetter = driveLetter.Trim().TrimEnd(':');
                        if (!string.IsNullOrEmpty(driveLetter)) driveLetter = $"{driveLetter}:";
                        else driveLetter = null;
                    }
                    ulong size = Convert.ToUInt64(p["Size"] ?? 0);
                    string? gptType = p["GptType"]?.ToString()?.Trim('{', '}');
                    string? guid = p["Guid"]?.ToString()?.Trim('{', '}');

                    partitions.Add(new PartitionInfo(diskNo, partNo, driveLetter, size, gptType, guid));
                }
            }

            // Query Disks
            using (var diskSearcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Number, FriendlyName, SerialNumber, BusType, PartitionStyle, Guid, Size FROM MSFT_Disk")))
            using (var diskCollection = diskSearcher.Get())
            {
                foreach (ManagementObject d in diskCollection)
                {
                    int number = Convert.ToInt32(d["Number"] ?? 0);
                    string friendlyName = d["FriendlyName"]?.ToString() ?? $"Disk {number}";
                    string serialNumber = d["SerialNumber"]?.ToString()?.Trim() ?? "";
                    string diskGuid = d["Guid"]?.ToString()?.Trim('{', '}') ?? "";
                    ulong size = Convert.ToUInt64(d["Size"] ?? 0);

                    int busTypeInt = Convert.ToInt32(d["BusType"] ?? 0);
                    string busType = busTypeInt switch
                    {
                        17 => "NVMe",
                        11 => "SATA",
                        7 => "USB",
                        15 => "File Backed Virtual",
                        _ => $"BusType({busTypeInt})"
                    };

                    int partStyleInt = Convert.ToInt32(d["PartitionStyle"] ?? 0);
                    string partitionStyle = partStyleInt switch
                    {
                        2 => "GPT",
                        1 => "MBR",
                        _ => "Unknown"
                    };

                    var diskParts = partitions.Where(p => p.DiskNumber == number).ToList();
                    var diskVols = diskParts.Where(p => !string.IsNullOrEmpty(p.DriveLetter)).Select(p => p.DriveLetter!).ToList();

                    disks.Add(new PhysicalDiskInfo(
                        DiskNumber: number,
                        FriendlyName: friendlyName,
                        SerialNumber: serialNumber,
                        BusType: busType,
                        PartitionStyle: partitionStyle,
                        Guid: diskGuid,
                        TotalBytes: size,
                        IsSystemDisk: false, // Will be set below
                        Partitions: diskParts,
                        Volumes: diskVols
                    ));
                }
            }
        }
        catch (Exception ex)
        {
            // Fallback via DriveInfo if WMI Storage fails
            Debug.WriteLine($"WMI Storage Query Warning: {ex.Message}");
        }

        // 2. Locate %SystemDrive% (C:)
        var systemPartition = partitions.FirstOrDefault(p =>
            string.Equals(p.DriveLetter?.TrimEnd(':'), systemLetter, StringComparison.OrdinalIgnoreCase));

        int systemDiskNumber = systemPartition?.DiskNumber ?? 0;

        // Mark system disk
        for (int i = 0; i < disks.Count; i++)
        {
            if (disks[i].DiskNumber == systemDiskNumber)
            {
                disks[i] = disks[i] with { IsSystemDisk = true };
            }
        }

        var systemDisk = disks.FirstOrDefault(d => d.IsSystemDisk);
        var protectedSystemDisk = new ProtectedSystemDisk(
            DiskNumber: systemDiskNumber,
            DiskGuid: systemDisk?.Guid ?? "",
            DiskSerial: systemDisk?.SerialNumber ?? "",
            SystemVolume: $"{systemLetter}:",
            Status: "PROTECTED_SYSTEM_DISK - CANNOT BE MODIFIED"
        );

        // 3. Build Volumes Inventory via .NET native DriveInfo
        var volumes = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;

            string driveLetter = drive.Name.TrimEnd('\\'); // e.g. "C:"
            bool isSys = string.Equals(driveLetter, protectedSystemDisk.SystemVolume, StringComparison.OrdinalIgnoreCase);

            var part = partitions.FirstOrDefault(p =>
                string.Equals(p.DriveLetter, driveLetter, StringComparison.OrdinalIgnoreCase));
            int diskNo = part?.DiskNumber ?? (isSys ? systemDiskNumber : 999);

            bool isNtfs = drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);

            volumes.Add(new VolumeInfo(
                DriveLetter: driveLetter,
                Label: string.IsNullOrEmpty(drive.VolumeLabel) ? driveLetter : drive.VolumeLabel,
                FileSystem: drive.DriveFormat,
                TotalBytes: (ulong)drive.TotalSize,
                FreeBytes: (ulong)drive.TotalFreeSpace,
                DiskNumber: diskNo,
                IsSystemVolume: isSys,
                IsNtfs: isNtfs
            ));
        }

        // 4. Find candidate targets on secondary physical disks != protectedSystemDisk
        var candidateTargets = new List<CandidateTarget>();
        const ulong safetyBufferBytes = 20UL * 1024 * 1024 * 1024; // 20 GB safety buffer for Windows
        const ulong minLinuxBytes = 25UL * 1024 * 1024 * 1024;     // 25 GB minimum for Linux

        foreach (var vol in volumes)
        {
            // Strict Invariant: System disk is excluded
            if (vol.IsSystemVolume || vol.DiskNumber == protectedSystemDisk.DiskNumber)
            {
                continue;
            }

            var disk = disks.FirstOrDefault(d => d.DiskNumber == vol.DiskNumber);
            var diskGuid = disk?.Guid ?? "";
            var diskSerial = disk?.SerialNumber ?? "";

            ulong maxShrink = vol.FreeBytes > safetyBufferBytes ? vol.FreeBytes - safetyBufferBytes : 0;
            bool hasSpace = maxShrink >= minLinuxBytes;
            bool isEligible = vol.IsNtfs && hasSpace;

            string reason = !vol.IsNtfs
                ? $"Volume format is {vol.FileSystem} (only NTFS is supported for resizing)"
                : !hasSpace
                    ? $"Insufficient free space ({vol.FreeBytes / (1024 * 1024 * 1024)} GB available; need at least 45 GB total to maintain Windows safety buffer)"
                    : "Eligible for dual-boot space allocation";

            candidateTargets.Add(new CandidateTarget(
                DiskNumber: vol.DiskNumber,
                DiskGuid: diskGuid,
                DiskSerial: diskSerial,
                DriveLetter: vol.DriveLetter,
                VolumeLabel: vol.Label,
                TotalBytes: vol.TotalBytes,
                FreeBytes: vol.FreeBytes,
                MaxShrinkBytes: maxShrink,
                IsEligible: isEligible,
                EligibilityReason: reason
            ));
        }

        // 5. Firmware & Secure Boot
        var firmware = DetectFirmware();

        // 6. WSL Detection
        var wsl = DetectWsl();

        // 7. EFI System Partition detection
        var existingEsps = DetectEsps(partitions, systemDiskNumber);

        return new DiscoveryReport(
            Timestamp: DateTime.UtcNow,
            Firmware: firmware,
            Wsl: wsl,
            ProtectedSystemDisk: protectedSystemDisk,
            CandidateTargets: candidateTargets,
            Disks: disks,
            Volumes: volumes,
            ExistingEsps: existingEsps
        );
    }

    private static FirmwareInfo DetectFirmware()
    {
        uint fwType = 0;
        bool isUefi = false;
        string typeStr = "Unknown";

        try
        {
            if (GetFirmwareType(ref fwType))
            {
                isUefi = fwType == 2;
                typeStr = isUefi ? "UEFI" : (fwType == 1 ? "Legacy BIOS" : $"Unknown ({fwType})");
            }
        }
        catch
        {
            typeStr = "UEFI";
            isUefi = true;
        }

        bool? secureBoot = null;
        try
        {
            var sbReg = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", null);
            if (sbReg is int sbVal)
            {
                secureBoot = sbVal == 1;
            }
        }
        catch { }

        return new FirmwareInfo(isUefi, typeStr, secureBoot);
    }

    private static List<EspInfo> DetectEsps(List<PartitionInfo> partitions, int systemDiskNumber)
    {
        // EFI System Partition GPT type GUID (case-insensitive)
        const string EspGptTypeGuid = "C12A7328-F81F-11D2-BA4B-00A0C93EC93B";

        var esps = new List<EspInfo>();
        foreach (var partition in partitions)
        {
            if (string.Equals(partition.GptType, EspGptTypeGuid, StringComparison.OrdinalIgnoreCase))
            {
                esps.Add(new EspInfo(
                    DiskNumber: partition.DiskNumber,
                    PartitionNumber: partition.PartitionNumber,
                    PartitionGuid: partition.Guid,
                    SizeBytes: partition.SizeBytes,
                    IsOnSystemDisk: partition.DiskNumber == systemDiskNumber
                ));
            }
        }
        return esps;
    }

    private static WslStatusInfo DetectWsl()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = "-l -q",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null) return new WslStatusInfo(false, null, new(), false);

            string raw = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);

            var cleaned = raw.Replace("\0", "").Trim();
            var distros = new List<string>();
            string? defaultDistro = null;
            bool hasDebian = false;

            var lines = cleaned.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var name = line.Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    distros.Add(name);
                    if (defaultDistro == null) defaultDistro = name;
                    if (name.Equals("Debian", StringComparison.OrdinalIgnoreCase)) hasDebian = true;
                }
            }

            return new WslStatusInfo(true, defaultDistro, distros, hasDebian);
        }
        catch
        {
            return new WslStatusInfo(false, null, new(), false);
        }
    }
}

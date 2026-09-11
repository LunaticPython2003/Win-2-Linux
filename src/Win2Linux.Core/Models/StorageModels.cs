using System.Text.Json.Serialization;

namespace Win2Linux.Core.Models;

public record PartitionInfo(
    int DiskNumber,
    int PartitionNumber,
    string? DriveLetter,
    ulong SizeBytes,
    string? GptType,
    string? Guid
);

public record VolumeInfo(
    string DriveLetter,
    string Label,
    string FileSystem,
    ulong TotalBytes,
    ulong FreeBytes,
    int DiskNumber,
    bool IsSystemVolume,
    bool IsNtfs
);

public record PhysicalDiskInfo(
    int DiskNumber,
    string FriendlyName,
    string SerialNumber,
    string BusType,
    string PartitionStyle,
    string? Guid,
    ulong TotalBytes,
    bool IsSystemDisk,
    List<PartitionInfo> Partitions,
    List<string> Volumes
);

public record ProtectedSystemDisk(
    int DiskNumber,
    string DiskGuid,
    string DiskSerial,
    string SystemVolume,
    string Status = "PROTECTED_SYSTEM_DISK - CANNOT BE MODIFIED"
);

public record CandidateTarget(
    int DiskNumber,
    string DiskGuid,
    string DiskSerial,
    string DriveLetter,
    string VolumeLabel,
    ulong TotalBytes,
    ulong FreeBytes,
    ulong MaxShrinkBytes,
    bool IsEligible,
    string EligibilityReason
);

public record FirmwareInfo(
    bool IsUefi,
    string FirmwareType,
    bool? SecureBootEnabled
);

public record WslStatusInfo(
    bool IsInstalled,
    string? DefaultDistro,
    List<string> InstalledDistros,
    bool HasDebianHelper
);

/// <summary>
/// Represents a detected EFI System Partition on a physical disk.
/// Used to decide whether to create a new ESP or reuse an existing one on the secondary disk.
/// </summary>
public record EspInfo(
    int DiskNumber,
    int PartitionNumber,
    string? PartitionGuid,
    ulong SizeBytes,
    bool IsOnSystemDisk
);

public record DiscoveryReport(
    DateTime Timestamp,
    FirmwareInfo Firmware,
    WslStatusInfo Wsl,
    ProtectedSystemDisk ProtectedSystemDisk,
    List<CandidateTarget> CandidateTargets,
    List<PhysicalDiskInfo> Disks,
    List<VolumeInfo> Volumes,
    List<EspInfo> ExistingEsps
);

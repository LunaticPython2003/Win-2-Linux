using Win2Linux.Core.Models;

namespace Win2Linux.Core.Safety;

public static class SafetyValidator
{
    /// <summary>
    /// Strictly verifies that a proposed target disk does NOT match the Windows system disk.
    /// Fails closed immediately if disk number, disk GUID, or disk serial matches the protected system disk.
    /// </summary>
    public static void AssertNotProtectedSystemDisk(
        int targetDiskNumber,
        string targetDiskGuid,
        string targetDiskSerial,
        ProtectedSystemDisk protectedDisk)
    {
        if (targetDiskNumber == protectedDisk.DiskNumber)
        {
            throw new InvalidOperationException(
                $"CRITICAL SAFETY VIOLATION: Proposed target disk (Disk {targetDiskNumber}) is the protected Windows system disk (Disk {protectedDisk.DiskNumber}). Operation ABORTED.");
        }

        if (!string.IsNullOrWhiteSpace(targetDiskGuid) &&
            targetDiskGuid.Trim('{', '}').Equals(protectedDisk.DiskGuid.Trim('{', '}'), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"CRITICAL SAFETY VIOLATION: Target disk GUID '{targetDiskGuid}' matches the protected Windows system disk GUID '{protectedDisk.DiskGuid}'. Operation ABORTED.");
        }

        if (!string.IsNullOrWhiteSpace(targetDiskSerial) &&
            targetDiskSerial.Trim().Equals(protectedDisk.DiskSerial.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"CRITICAL SAFETY VIOLATION: Target disk serial '{targetDiskSerial}' matches the protected Windows system disk serial '{protectedDisk.DiskSerial}'. Operation ABORTED.");
        }
    }

    /// <summary>
    /// Verifies that a target drive letter is not %SystemDrive% (C:).
    /// </summary>
    public static void AssertNotSystemVolume(string targetVolume, ProtectedSystemDisk protectedDisk)
    {
        var cleanTarget = targetVolume.TrimEnd('\\', '/').ToUpperInvariant();
        var cleanSystem = protectedDisk.SystemVolume.TrimEnd('\\', '/').ToUpperInvariant();

        if (cleanTarget == cleanSystem)
        {
            throw new InvalidOperationException(
                $"CRITICAL SAFETY VIOLATION: Proposed target volume '{targetVolume}' is the protected Windows system volume '{protectedDisk.SystemVolume}'. Operation ABORTED.");
        }
    }
}

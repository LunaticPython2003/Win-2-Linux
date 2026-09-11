using Win2Linux.Core.Models;
using Win2Linux.Core.Safety;

namespace Win2Linux.Tests;

public class SafetyTests
{
    private readonly ProtectedSystemDisk _protectedDisk = new(
        DiskNumber: 0,
        DiskGuid: "94742ff6-a69f-49c1-80c4-07eb17c0a2cd",
        DiskSerial: "0025_38F6_51CE_9992.",
        SystemVolume: "C:",
        Status: "PROTECTED_SYSTEM_DISK - CANNOT BE MODIFIED"
    );

    [Fact]
    public void RejectsTargetMatchingSystemDiskNumber()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SafetyValidator.AssertNotProtectedSystemDisk(
                targetDiskNumber: 0,
                targetDiskGuid: "5c82fe68-fa5b-4ecf-acf1-a6050100048e",
                targetDiskSerial: "6479_A750_20C0_09A4.",
                _protectedDisk));

        Assert.Contains("protected Windows system disk", ex.Message);
    }

    [Fact]
    public void RejectsTargetMatchingSystemDiskGuid()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SafetyValidator.AssertNotProtectedSystemDisk(
                targetDiskNumber: 1,
                targetDiskGuid: "{94742ff6-a69f-49c1-80c4-07eb17c0a2cd}",
                targetDiskSerial: "6479_A750_20C0_09A4.",
                _protectedDisk));

        Assert.Contains("matches the protected Windows system disk GUID", ex.Message);
    }

    [Fact]
    public void RejectsTargetMatchingSystemDiskSerial()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SafetyValidator.AssertNotProtectedSystemDisk(
                targetDiskNumber: 1,
                targetDiskGuid: "5c82fe68-fa5b-4ecf-acf1-a6050100048e",
                targetDiskSerial: "0025_38F6_51CE_9992.",
                _protectedDisk));

        Assert.Contains("matches the protected Windows system disk serial", ex.Message);
    }

    [Fact]
    public void RejectsTargetMatchingSystemVolumeC()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SafetyValidator.AssertNotSystemVolume("C:", _protectedDisk));

        Assert.Contains("protected Windows system volume", ex.Message);
    }

    [Fact]
    public void AcceptsValidSecondaryDiskD()
    {
        // Must succeed without throwing
        SafetyValidator.AssertNotProtectedSystemDisk(
            targetDiskNumber: 1,
            targetDiskGuid: "5c82fe68-fa5b-4ecf-acf1-a6050100048e",
            targetDiskSerial: "6479_A750_20C0_09A4.",
            _protectedDisk);

        SafetyValidator.AssertNotSystemVolume("D:", _protectedDisk);
    }
}

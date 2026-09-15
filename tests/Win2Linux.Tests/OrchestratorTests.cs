using Win2Linux.Core.Distros;
using Win2Linux.Core.Models;
using Win2Linux.Core.Orchestrator;

namespace Win2Linux.Tests;

public class OrchestratorTests
{
    private readonly ProtectedSystemDisk _protectedDisk = new(
        DiskNumber: 0,
        DiskGuid: "94742ff6-a69f-49c1-80c4-07eb17c0a2cd",
        DiskSerial: "0025_38F6_51CE_9992.",
        SystemVolume: "C:",
        Status: "PROTECTED_SYSTEM_DISK - CANNOT BE MODIFIED"
    );

    [Fact]
    public async Task StagingRejectsViolationOfSystemDiskInvariant()
    {
        var targetSystemDisk = new CandidateTarget(
            DiskNumber: 0,
            DiskGuid: "94742ff6-a69f-49c1-80c4-07eb17c0a2cd",
            DiskSerial: "0025_38F6_51CE_9992.",
            DriveLetter: "C:",
            VolumeLabel: "Windows",
            TotalBytes: 1023348146176,
            FreeBytes: 500000000000,
            MaxShrinkBytes: 400000000000,
            IsEligible: false,
            EligibilityReason: "Cannot use system disk"
        );

        var plan = new InstallationPlan(
            DistroRegistry.SupportedDistributions[0],
            targetSystemDisk,
            _protectedDisk,
            53687091200,
            50
        );

        var updates = new List<ProgressUpdate>();
        var progress = new Progress<ProgressUpdate>(updates.Add);

        var result = await InstallationOrchestrator.ExecuteStagingAsync(plan, progress);

        Assert.False(result);
        Assert.Contains(updates, u => u.ErrorMessage != null && u.ErrorMessage.Contains("CRITICAL SAFETY VIOLATION"));
    }
}

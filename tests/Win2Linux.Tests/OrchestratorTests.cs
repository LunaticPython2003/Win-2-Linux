using Win2Linux.Core.Distros;
using Win2Linux.Core.Models;
using Win2Linux.Core.Orchestrator;
using Xunit;

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

    private readonly CandidateTarget _targetSecondaryDisk = new(
        DiskNumber: 1,
        DiskGuid: "c12a7328-f81f-11d2-ba4b-00a0c93ec93b",
        DiskSerial: "GIGABYTE_GP_GSM2.",
        DriveLetter: "D:",
        VolumeLabel: "Data",
        TotalBytes: 852392673280,
        FreeBytes: 500000000000,
        MaxShrinkBytes: 400000000000,
        IsEligible: true,
        EligibilityReason: "Eligible secondary volume"
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

    [Fact]
    public void FedoraUnattendedConfigGeneratesKdePlasmaByDefaultForUser()
    {
        var fedora = DistroRegistry.SupportedDistributions.First(d => d.Id == "fedora");
        var plan = new InstallationPlan(
            fedora,
            _targetSecondaryDisk,
            _protectedDisk,
            53687091200,
            50
        );

        var kickstart = InstallationOrchestrator.GenerateUnattendedConfig(plan);
        Assert.Contains("@^kde-desktop-environment", kickstart);
        Assert.DoesNotContain("@^workstation-product-environment", kickstart);
    }

    [Fact]
    public void FedoraUnattendedConfigGeneratesGnomeWhenExplicitlySelected()
    {
        var fedoraGnome = DistroRegistry.SupportedDistributions
            .First(d => d.Id == "fedora")
            .WithDesktopEnvironment("gnome");

        var plan = new InstallationPlan(
            fedoraGnome,
            _targetSecondaryDisk,
            _protectedDisk,
            53687091200,
            50
        );

        var kickstart = InstallationOrchestrator.GenerateUnattendedConfig(plan);
        Assert.Contains("@^workstation-product-environment", kickstart);
        Assert.DoesNotContain("@^kde-desktop-environment", kickstart);
    }

    [Fact]
    public void LinuxMintAndZorinGenerateValidDualBootConfigs()
    {
        var mint = DistroRegistry.SupportedDistributions.First(d => d.Id == "linuxmint");
        var mintPlan = new InstallationPlan(mint, _targetSecondaryDisk, _protectedDisk, 53687091200, 50);
        var mintConfig = InstallationOrchestrator.GenerateUnattendedConfig(mintPlan);
        Assert.Contains("linuxmint-dualboot", mintConfig);
        Assert.Contains("biggest_free", mintConfig);

        var zorin = DistroRegistry.SupportedDistributions.First(d => d.Id == "zorin");
        var zorinPlan = new InstallationPlan(zorin, _targetSecondaryDisk, _protectedDisk, 53687091200, 50);
        var zorinConfig = InstallationOrchestrator.GenerateUnattendedConfig(zorinPlan);
        Assert.Contains("zorin-dualboot", zorinConfig);
        Assert.Contains("autoinstall", zorinConfig);
    }
}

using System;
using System.Collections.Generic;
using Win2Linux.Core.Models;
using Win2Linux.Core.Orchestrator;
using Win2Linux.Core.Distros;

if (args.Length == 1)
{
    InstallationOrchestrator.ValidateAutoinstallYaml(System.IO.File.ReadAllText(args[0]));
    Console.WriteLine("Autoinstall YAML validation passed.");
    return;
}

var plan = new InstallationPlan(
    SelectedDistro: DistroRegistry.SupportedDistributions[0], // Ubuntu
    Target: new CandidateTarget(
        DiskNumber: 1,
        DiskGuid: "12345678-1234-1234-1234-123456789012",
        WindowsDiskSerial: "SAMSUNG_MZV8V1T0_S676NJ0R323385N",
        LinuxDiskSerial: "SAMSUNG_MZV8V1T0_S676NJ0R323385N_linux",
        DriveLetter: "D:",
        VolumeLabel: "Data",
        TotalBytes: 1000000000000,
        FreeBytes:  500000000000,
        MaxShrinkBytes: 400000000000,
        IsEligible: true,
        EligibilityReason: "Mock",
        Partitions: new List<PartitionInfo>
        {
            new PartitionInfo(1, 1, null, 16759808, "e3c9e316-0b5c-4db8-817d-f92df00215ae", null, 17408),
            new PartitionInfo(1, 2, "D:", 809444384256, "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7", null, 16777216)
        }
    ),
    ReservedBytes: 50000000000,
    ProtectedDisk: new ProtectedSystemDisk(
        DiskNumber: 0,
        DiskGuid: "00000000-0000-0000-0000-000000000000",
        WindowsDiskSerial: "C_DRIVE_SERIAL",
        SystemVolume: "C:",
        Status: "PROTECTED"
    ),
    ReservedGb: 50.0,
    SetupConfig: new LinuxSetupConfiguration
    {
        InteractiveReview = false,
        InstallNvidiaDrivers = true,
        Username = "testuser",
        Hostname = "test-pc",
        Password = "validpassword123"
    }
);

// We simulate that the ESP will be partition 3 (since partitions 1 and 2 exist)
string autoinstallYaml = InstallationOrchestrator.GenerateUnattendedConfig(
    plan, espPartitionNumber: 3, espSizeBytes: 8589934592,
    espPartitionGuid: "aabbccdd-eeff-0011-2233-445566778899", espOffsetBytes: 809461874688);

Console.WriteLine("----- BEGIN AUTOINSTALL.YAML -----");
Console.WriteLine(autoinstallYaml);
Console.WriteLine("----- END AUTOINSTALL.YAML -----");

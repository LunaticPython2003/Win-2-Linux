using System.Text.Json;
using Win2Linux.Core.Distros;
using Win2Linux.Core.Storage;

using Win2Linux.Core.Orchestrator;

namespace Win2Linux.Cli;

class Program
{
    static void Main(string[] args)
    {
        bool jsonMode = args.Contains("--json");
        bool distrosMode = args.Contains("distros") || args.Contains("--distros");
        bool stageMode = args.Contains("stage") || args.Contains("--stage");

        if (distrosMode)
        {
            var distros = DistroRegistry.SupportedDistributions;
            if (jsonMode)
            {
                Console.WriteLine(JsonSerializer.Serialize(distros, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine("================================================================================");
                Console.WriteLine("          Win2Linux — Secure-Boot Certified Linux Distributions                ");
                Console.WriteLine("================================================================================");
                Console.WriteLine();
                foreach (var d in distros)
                {
                    Console.WriteLine($"[+] {d.DisplayName} (ID: {d.Id})");
                    Console.WriteLine($"    Description:        {d.Description}");
                    Console.WriteLine($"    Secure Boot:        {d.SecureBootStatus} via {d.SecureBootSigner}");
                    Console.WriteLine($"    Default Filesystem: {d.DefaultFilesystem}");
                    Console.WriteLine($"    EFI Vendor Path:    \\EFI\\{d.EfiVendorDir}\\{d.EfiBinary}");
                    Console.WriteLine($"    UEFI NVRAM Label:   {d.UefiBootLabel}");
                    Console.WriteLine($"    Unattended Install: {d.UnattendedMechanism}");
                    Console.WriteLine();
                }
            }
            return;
        }

        var report = StorageDiscoveryService.RunDiscovery();

        if (stageMode)
        {
            var target = report.CandidateTargets.FirstOrDefault(t => t.IsEligible);
            if (target == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: No eligible secondary storage target found.");
                Console.ResetColor();
                return;
            }

            var distro = DistroRegistry.SupportedDistributions[0];
            double sizeGb = 100;
            var plan = new InstallationPlan(distro, target, report.ProtectedSystemDisk, (ulong)(sizeGb * 1024 * 1024 * 1024), sizeGb);
            var progress = new Progress<ProgressUpdate>(u =>
            {
                Console.WriteLine($"[{u.Percent,3:F0}%] {u.LogMessage}");
            });

            Console.WriteLine("================================================================================");
            Console.WriteLine("          Win2Linux — Executing Dual-Boot Staging                              ");
            Console.WriteLine("================================================================================");
            var success = InstallationOrchestrator.ExecuteStagingAsync(plan, progress).GetAwaiter().GetResult();
            Console.WriteLine(success ? "Staging completed successfully!" : "Staging failed.");
            return;
        }

        if (jsonMode)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine("================================================================================");
        Console.WriteLine("          Win2Linux — Safe Preflight Storage Discovery Report                  ");
        Console.WriteLine("================================================================================");
        Console.WriteLine();

        Console.WriteLine("FIRMWARE & SECURITY:");
        Console.WriteLine($"  Boot Mode:       {report.Firmware.FirmwareType}");
        var sb = report.Firmware.SecureBootEnabled switch
        {
            true => "ENABLED (Secure Boot Active)",
            false => "DISABLED",
            null => "UNKNOWN / NOT DETECTED"
        };
        Console.WriteLine($"  Secure Boot:     {sb}");
        Console.WriteLine();

        Console.WriteLine("WSL HELPER ENVIRONMENT:");
        Console.WriteLine($"  WSL Installed:   {(report.Wsl.IsInstalled ? "YES" : "NO")}");
        if (report.Wsl.DefaultDistro != null)
            Console.WriteLine($"  Default Distro:  {report.Wsl.DefaultDistro}");
        Console.WriteLine($"  Distros:         {string.Join(", ", report.Wsl.InstalledDistros)}");
        Console.WriteLine($"  Debian Helper:   {(report.Wsl.HasDebianHelper ? "READY" : "NOT INSTALLED (will be auto-provisioned)")}");
        Console.WriteLine();

        Console.WriteLine("PROTECTED WINDOWS SYSTEM DISK (NON-MODIFIABLE):");
        Console.WriteLine($"  Disk Number:     Disk {report.ProtectedSystemDisk.DiskNumber}");
        Console.WriteLine($"  System Volume:   {report.ProtectedSystemDisk.SystemVolume}");
        Console.WriteLine($"  Disk Serial:     {report.ProtectedSystemDisk.DiskSerial}");
        Console.WriteLine($"  Disk GUID:       {report.ProtectedSystemDisk.DiskGuid}");
        Console.WriteLine($"  Status:          🔒 {report.ProtectedSystemDisk.Status}");
        Console.WriteLine();

        Console.WriteLine("ELIGIBLE SECONDARY TARGETS (SAFE TO SHRINK FOR DUAL-BOOT):");
        if (report.CandidateTargets.Count == 0)
        {
            Console.WriteLine("  [!] No eligible secondary targets found.");
        }
        else
        {
            foreach (var target in report.CandidateTargets)
            {
                var totalGb = target.TotalBytes / (1024 * 1024 * 1024);
                var freeGb = target.FreeBytes / (1024 * 1024 * 1024);
                var maxShrinkGb = target.MaxShrinkBytes / (1024 * 1024 * 1024);

                Console.WriteLine($"  Drive {target.DriveLetter} [{target.VolumeLabel}] on Disk {target.DiskNumber}");
                Console.WriteLine($"    Total Size:    {totalGb} GB");
                Console.WriteLine($"    Free Space:    {freeGb} GB");
                Console.WriteLine($"    Max For Linux: {maxShrinkGb} GB (leaving 20 GB safety buffer for Windows)");
                Console.WriteLine($"    Status:        {(target.IsEligible ? "ELIGIBLE" : "INELIGIBLE")}");
                Console.WriteLine($"    Note:          {target.EligibilityReason}");
                Console.WriteLine();
            }
        }

        Console.WriteLine("PHYSICAL DISKS INVENTORY:");
        foreach (var d in report.Disks)
        {
            var lockTag = d.IsSystemDisk ? "🔒 [PROTECTED_SYSTEM_DISK]" : "✓ [SECONDARY_DISK]";
            var sizeGb = d.TotalBytes / (1024 * 1024 * 1024);
            Console.WriteLine($"  Disk {d.DiskNumber}: {d.FriendlyName} ({sizeGb} GB, {d.BusType}) {lockTag}");
            Console.WriteLine($"    Serial:        {d.SerialNumber}");
            if (!string.IsNullOrEmpty(d.Guid))
                Console.WriteLine($"    GPT GUID:      {d.Guid}");
            Console.WriteLine($"    Volumes:       {string.Join(", ", d.Volumes)}");
            Console.WriteLine();
        }
    }
}

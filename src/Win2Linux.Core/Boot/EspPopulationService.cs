using System.IO;
using Win2Linux.Core.Boot;
using Win2Linux.Core.Common;
using Win2Linux.Core.Distros;
using Win2Linux.Core.Extract;
using Win2Linux.Core.Orchestrator;

namespace Win2Linux.Core.Boot;

/// <summary>
/// Populates the newly created FAT32 ESP with the full EFI tree and installer payload.
///
/// Final ESP structure:
///
///   ESP:\
///   ├── EFI\
///   │   ├── BOOT\
///   │   │   ├── BOOTX64.EFI         ← shim (universal UEFI fallback — spec-guaranteed to boot)
///   │   │   ├── grubx64.efi         ← GRUB binary (shim executes this from same dir)
///   │   │   └── grub.cfg            ← fallback config
///   │   └── &lt;vendor&gt;\             ← e.g. ubuntu\, fedora\
///   │       ├── shimx64.efi         ← shim copy for vendor path
///   │       ├── grubx64.efi         ← GRUB EFI binary
///   │       └── grub.cfg            ← generated — loads vmlinuz + autoinstall
///   └── win2linux\
///       ├── boot\
///       │   ├── vmlinuz             ← Linux kernel from ISO
///       │   └── initrd              ← initramfs from ISO
///       └── unattended\
///           ├── autoinstall.yaml    ← Subiquity configuration
///           └── meta-data           ← empty (required by cloud-init nocloud)
/// </summary>
public static class EspPopulationService
{
    /// <summary>
    /// Writes all required files to the mounted ESP.
    /// </summary>
    /// <param name="espRoot">The root path of the mounted ESP, e.g. "W:\"</param>
    /// <param name="bootStagingDir">Directory containing extracted vmlinuz, initrd, and optionally LiveOS/squashfs.img</param>
    /// <param name="efiStagingDir">Directory containing extracted BOOTX64.EFI and grubx64.efi</param>
    /// <param name="unattendedDir">Directory containing the generated autoinstall.yaml</param>
    public static async Task PopulateEspAsync(
        DistroProfile distro,
        string espRoot,
        string bootStagingDir,
        string efiStagingDir,
        string unattendedDir,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct = default)
    {
        espRoot = espRoot.TrimEnd('\\', '/');

        progress.Report(new ProgressUpdate(9, 10, "Populating EFI Environment", 74,
            $"[ESP Population] Writing EFI tree to {espRoot}\\..."));

        // --- 1. Create directory structure ---
        var efiBootDir = Path.Combine(espRoot, "EFI", "BOOT");
        var efiVendorDir = Path.Combine(espRoot, "EFI", distro.EfiVendorDir);
        var espBootDir = Path.Combine(espRoot, "win2linux", "boot");
        var espUnattendedDir = Path.Combine(espRoot, "win2linux", "unattended");

        Directory.CreateDirectory(efiBootDir);
        Directory.CreateDirectory(efiVendorDir);
        Directory.CreateDirectory(espBootDir);
        Directory.CreateDirectory(espUnattendedDir);

        // --- 2. Write EFI\BOOT\BOOTX64.EFI (universal UEFI fallback) ---
        // This is the spec-mandated path that ALL UEFI firmware must look for.
        // We place a copy of shimx64.efi here so the installer boots without any NVRAM entry.
        var shimSource = Path.Combine(efiStagingDir, "BOOTX64.EFI");
        var bootX64Dest = Path.Combine(efiBootDir, "BOOTX64.EFI");
        FileUtilities.SafeCopy(shimSource, bootX64Dest);

        // --- 3. Write EFI\BOOT\grubx64.efi and EFI\BOOT\grub.cfg ---
        // CRITICAL: shim (BOOTX64.EFI) executed from EFI\BOOT\ looks for grubx64.efi
        // in the SAME directory (\EFI\BOOT\grubx64.efi). Without this, booting "EFI Hard drive"
        // fails with "Failed to open \EFI\BOOT\grubx64.efi - Not Found".
        var grubSource = Path.Combine(efiStagingDir, "grubx64.efi");
        var grubBootDest = Path.Combine(efiBootDir, "grubx64.efi");
        FileUtilities.SafeCopy(grubSource, grubBootDest);

        var grubCfgContent = GenerateInstallerGrubConfig(distro);
        var grubBootCfgDest = Path.Combine(efiBootDir, "grub.cfg");
        await File.WriteAllTextAsync(grubBootCfgDest, grubCfgContent, ct);

        progress.Report(new ProgressUpdate(9, 10, "Populating EFI Environment", 75,
            $"[ESP Population] ✓ EFI\\BOOT\\ (BOOTX64.EFI + grubx64.efi + grub.cfg) written (universal fallback)"));

        // --- 4. Write EFI\<vendor>\shimx64.efi ---
        var shimVendorDest = Path.Combine(efiVendorDir, "shimx64.efi");
        FileUtilities.SafeCopy(Path.Combine(efiStagingDir, "shimx64.efi"), shimVendorDest);

        // --- 5. Write EFI\<vendor>\grubx64.efi ---
        var grubVendorDest = Path.Combine(efiVendorDir, "grubx64.efi");
        FileUtilities.SafeCopy(grubSource, grubVendorDest);

        // --- 6. Write EFI\<vendor>\grub.cfg ---
        var grubCfgDest = Path.Combine(efiVendorDir, "grub.cfg");
        await File.WriteAllTextAsync(grubCfgDest, grubCfgContent, ct);

        // Copy mmx64.efi (MokManager) if present in staging
        var mmxSource = Path.Combine(efiStagingDir, "mmx64.efi");
        if (File.Exists(mmxSource))
        {
            FileUtilities.SafeCopy(mmxSource, Path.Combine(efiBootDir, "mmx64.efi"));
            FileUtilities.SafeCopy(mmxSource, Path.Combine(efiVendorDir, "mmx64.efi"));
        }

        progress.Report(new ProgressUpdate(9, 10, "Populating EFI Environment", 78,
            $"[ESP Population] ✓ EFI\\{distro.EfiVendorDir}\\ written with vendor shim, GRUB, and grub.cfg"));

        // --- 7. Write win2linux\boot\vmlinuz and initrd ---
        var vmlinuzSrc = Path.Combine(bootStagingDir, "vmlinuz");
        var initrdSrc = Path.Combine(bootStagingDir, "initrd");
        var vmlinuzDest = Path.Combine(espBootDir, "vmlinuz");
        var initrdDest = Path.Combine(espBootDir, "initrd");

        FileUtilities.SafeCopy(vmlinuzSrc, vmlinuzDest);
        progress.Report(new ProgressUpdate(9, 10, "Populating EFI Environment", 80,
            $"[ESP Population] ✓ vmlinuz copied ({new FileInfo(vmlinuzDest).Length / (1024 * 1024)} MB)"));

        FileUtilities.SafeCopy(initrdSrc, initrdDest);
        progress.Report(new ProgressUpdate(9, 10, "Populating EFI Environment", 83,
            $"[ESP Population] ✓ initrd copied ({new FileInfo(initrdDest).Length / (1024 * 1024)} MB)"));

        // --- 8a. Copy LiveOS/squashfs.img for Fedora/openSUSE (dracut rd.live.image requirement) ---
        // dracut's live boot module resolves `root=live:CDLABEL=LINUXEFI` by scanning the ESP
        // for a file named LiveOS/squashfs.img relative to the partition root.
        // Without this image present on the ESP, dracut cannot mount the root filesystem
        // and drops to an emergency shell with "dracut-mount: Can't mount root filesystem".
        string? squashfsDest = null;
        if (IsoExtractService.RequiresLiveSquashfs(distro))
        {
            var squashfsSrc = Path.Combine(bootStagingDir, "LiveOS", "squashfs.img");
            if (!File.Exists(squashfsSrc))
            {
                throw new EspPopulationException(
                    $"LiveOS/squashfs.img not found in boot staging dir '{bootStagingDir}'. " +
                    $"For {distro.DisplayName}, IsoExtractService.ExtractLiveSquashfsAsync() must be " +
                    "called before PopulateEspAsync(). This file is required for dracut live boot.");
            }

            var squashfsEspDir = Path.Combine(espRoot, "LiveOS");
            Directory.CreateDirectory(squashfsEspDir);
            squashfsDest = Path.Combine(squashfsEspDir, "squashfs.img");
            FileUtilities.SafeCopy(squashfsSrc, squashfsDest);

            var sizeMb = new FileInfo(squashfsDest).Length / (1024 * 1024);
            progress.Report(new ProgressUpdate(9, 10, "Populating EFI Environment", 84,
                $"[ESP Population] ✓ LiveOS/squashfs.img ({sizeMb} MB) copied to ESP — dracut live root filesystem"));
        }

        // --- 8. Copy autoinstall.yaml ---
        var unattendedFilename = GetUnattendedFilename(distro.Id);
        var unattendedSrc = Path.Combine(unattendedDir, unattendedFilename);
        var unattendedDest = Path.Combine(espUnattendedDir, unattendedFilename);

        if (File.Exists(unattendedSrc))
        {
            FileUtilities.SafeCopy(unattendedSrc, unattendedDest);
        }
        else
        {
            throw new EspPopulationException(
                $"Unattended config not found at '{unattendedSrc}'. " +
                "Ensure staging generated the config before ESP population.");
        }

        // --- 9. Write meta-data (required by cloud-init nocloud datasource) ---
        // cloud-init requires both user-data (autoinstall.yaml) and meta-data files.
        // meta-data can be empty but must exist.
        var metaDataDest = Path.Combine(espUnattendedDir, "meta-data");
        await File.WriteAllTextAsync(metaDataDest, "instance-id: win2linux-installer\n", ct);

        progress.Report(new ProgressUpdate(9, 10, "Populating EFI Environment", 85,
            $"[ESP Population] ✓ Autoinstall payload and meta-data written"));

        // --- 10. Final structural verification ---
        var requiredList = new List<string>
        {
            bootX64Dest,
            grubBootDest,
            grubBootCfgDest,
            shimVendorDest,
            grubVendorDest,
            grubCfgDest,
            vmlinuzDest,
            initrdDest,
            unattendedDest,
            metaDataDest,
        };

        // For Fedora/openSUSE: squashfs.img must also be on the ESP
        // (dracut rd.live.image mounts this as the root filesystem)
        if (squashfsDest != null)
        {
            requiredList.Add(squashfsDest);
        }

        foreach (var f in requiredList)
        {
            if (!File.Exists(f))
            {
                throw new EspPopulationException($"ESP population verification failed: '{f}' is missing.");
            }
        }

        progress.Report(new ProgressUpdate(9, 10, "EFI Environment Complete", 87,
            $"[ESP Population] ✓ All {requiredList.Count} required files verified on ESP. Boot environment is ready."));
    }

    /// <summary>
    /// Generates a grub.cfg for the Linux installer environment.
    /// Uses GRUB's search --file to locate the ESP partition dynamically — no hardcoded partition numbers.
    /// </summary>
    private static string GenerateInstallerGrubConfig(DistroProfile distro)
    {
        return $$"""
            # Win2Linux Installer GRUB Configuration
            # Auto-generated for {{distro.DisplayName}}
            # DO NOT EDIT — this file is managed by Win2Linux

            set default=0
            set timeout=8

            insmod part_gpt
            insmod fat
            insmod chain
            insmod echo

            if [ x$grub_platform = xxen ]; then insmod xzio; insmod lzopio; fi

            # Dynamically locate the ESP partition by searching for the installer vmlinuz.
            # This avoids hardcoded (hd0,gpt2) references that would break if disk order changes.
            search --no-floppy --file /win2linux/boot/vmlinuz --set=root

            menuentry "Install {{distro.DisplayName}} (Automated Dual-Boot)" --class linux {
                echo "Win2Linux: Loading {{distro.DisplayName}} installer kernel..."
                linux  /win2linux/boot/vmlinuz {{distro.AutoinstallKernelArgs}}
                initrd /win2linux/boot/initrd
            }

            menuentry "Windows Boot Manager" --class windows {
                echo "Win2Linux: Chainloading Windows Boot Manager..."
                search --no-floppy --file /EFI/Microsoft/Boot/bootmgfw.efi --set=root
                chainloader /EFI/Microsoft/Boot/bootmgfw.efi
            }

            menuentry "UEFI Firmware Settings" {
                fwsetup
            }
            """;
    }

    private static string GetUnattendedFilename(string distroId) => distroId.ToLowerInvariant() switch
    {
        "ubuntu" => "autoinstall.yaml",
        "fedora" => "kickstart.ks",
        "linuxmint" => "preseed.cfg",
        "zorin" => "autoinstall.yaml",
        _ => "autoinstall.yaml"
    };
}

public class EspPopulationException(string message) : Exception(message);

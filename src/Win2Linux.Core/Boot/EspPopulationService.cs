using System.IO;
using Win2Linux.Core.Boot;
using Win2Linux.Core.Common;
using Win2Linux.Core.Distros;
using Win2Linux.Core.Extract;
using Win2Linux.Core.Models;
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
///   │   └── <vendor>\               ← e.g. ubuntu\, fedora\
///   │       ├── shimx64.efi         ← shim copy for vendor path
///   │       ├── grubx64.efi         ← GRUB EFI binary
///   │       └── grub.cfg            ← generated — loads vmlinuz + autoinstall
///   └── win2linux\
///       ├── boot\
///       │   ├── vmlinuz             ← Linux kernel from ISO
///       │   ├── initrd              ← initramfs from ISO
///       │   └── win2linux.cpio      ← initrd overlay hook for live automated installation
///       └── unattended\             ← NoCloud seed for distros that use it
///   autoinstall.yaml                 ← Ubuntu Subiquity config, loaded directly from /cdrom
/// </summary>
public static class EspPopulationService
{
    /// <summary>
    /// Writes all required files to the mounted ESP.
    /// </summary>
    /// <param name="espRoot">The root path of the mounted ESP, e.g. "W:\"</param>
    /// <param name="bootStagingDir">Directory containing extracted vmlinuz, initrd, and optionally LiveOS/squashfs.img</param>
    /// <param name="efiStagingDir">Directory containing extracted BOOTX64.EFI and grubx64.efi</param>
    /// <param name="unattendedDir">Directory containing the generated autoinstall.yaml / kickstart.ks</param>
    public static async Task PopulateEspAsync(
        DistroProfile distro,
        string espRoot,
        string bootStagingDir,
        string efiStagingDir,
        string unattendedDir,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct = default,
        LinuxSetupConfiguration? setupConfig = null)
    {
        // Keep W:\ rooted. Trimming its slash would make W:EFI drive-relative.
        espRoot = Path.GetFullPath(espRoot);

        // --- 0. Validate Payload Size limits ---
        long totalPayloadBytes = 0;
        foreach (var dir in new[] { bootStagingDir, efiStagingDir, unattendedDir })
        {
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.Length >= 4294967295L)
                    {
                        throw new EspPopulationException($"File {fileInfo.Name} in {dir} exceeds FAT32 4GB limit ({fileInfo.Length} bytes). The file is too large for an EFI system partition.");
                    }
                    totalPayloadBytes += fileInfo.Length;
                }
            }
        }
        
        // Ensure the total payload does not exceed the available space on the ESP (8192 MB partition minus ~32 MB FAT32 filesystem overhead)
        long maxEspBytes = 8160L * 1024 * 1024;
        if (totalPayloadBytes > maxEspBytes)
        {
            throw new EspPopulationException($"Total installation payload ({totalPayloadBytes / (1024 * 1024)} MB) exceeds the available space on the 8192 MB EFI System Partition. Please check your ISO or extraction settings.");
        }

        progress.Report(new ProgressUpdate(8, 9, "Populating EFI Environment", 74,
            $"[ESP Population] Writing EFI tree to {espRoot}\\..."));

        // --- 1. Create directory structure ---
        var efiBootDir = Path.Combine(espRoot, "EFI", "boot");
        var efiVendorDir = Path.Combine(espRoot, "EFI", distro.EfiVendorDir);
        var espBootDir = Path.Combine(espRoot, "win2linux", "boot");
        var espUnattendedDir = Path.Combine(espRoot, "win2linux", "unattended");
        
        // Ensure GRUB finds its config regardless of its hardcoded prefix (Ubuntu uses /boot/grub, Fedora uses /boot/grub2)
        var bootGrubDir = Path.Combine(espRoot, "boot", "grub");
        var bootGrub2Dir = Path.Combine(espRoot, "boot", "grub2");

        Directory.CreateDirectory(efiBootDir);
        Directory.CreateDirectory(efiVendorDir);
        Directory.CreateDirectory(espBootDir);
        if (!distro.Id.Equals("ubuntu", StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(espUnattendedDir);
        }
        Directory.CreateDirectory(bootGrubDir);
        Directory.CreateDirectory(bootGrub2Dir);

        // --- 2. Write EFI\boot\bootx64.efi (universal UEFI fallback) ---
        // This is the spec-mandated path that ALL UEFI firmware must look for.
        // We place a copy of shimx64.efi here so the installer boots without any NVRAM entry.
        var shimSource = Path.Combine(efiStagingDir, "BOOTX64.EFI");
        var bootX64Dest = Path.Combine(efiBootDir, "bootx64.efi");
        FileUtilities.SafeCopy(shimSource, bootX64Dest);

        // --- 3. Write EFI\boot\grubx64.efi and EFI\boot\grub.cfg ---
        // CRITICAL: shim (bootx64.efi) executed from EFI\boot\ looks for grubx64.efi
        // in the SAME directory (\EFI\boot\grubx64.efi). Without this, booting "EFI Hard drive"
        // fails with "Failed to open \EFI\boot\grubx64.efi - Not Found".
        var grubSource = Path.Combine(efiStagingDir, "grubx64.efi");
        var grubBootDest = Path.Combine(efiBootDir, "grubx64.efi");
        FileUtilities.SafeCopy(grubSource, grubBootDest);

        var grubCfgContent = GenerateInstallerGrubConfig(distro, setupConfig);
        var grubBootCfgDest = Path.Combine(efiBootDir, "grub.cfg");
        await File.WriteAllTextAsync(grubBootCfgDest, grubCfgContent, ct);

        progress.Report(new ProgressUpdate(8, 9, "Populating EFI Environment", 75,
            $"[ESP Population] ✓ EFI\\boot\\ (bootx64.efi + grubx64.efi + grub.cfg) written (universal fallback)"));

        // --- 4. Write EFI\<vendor>\shimx64.efi ---
        var shimVendorDest = Path.Combine(efiVendorDir, "shimx64.efi");
        FileUtilities.SafeCopy(Path.Combine(efiStagingDir, "shimx64.efi"), shimVendorDest);

        // --- 5. Write EFI\<vendor>\grubx64.efi ---
        var grubVendorDest = Path.Combine(efiVendorDir, "grubx64.efi");
        FileUtilities.SafeCopy(grubSource, grubVendorDest);

        // --- 6. Write EFI\<vendor>\grub.cfg ---
        var grubCfgDest = Path.Combine(efiVendorDir, "grub.cfg");
        await File.WriteAllTextAsync(grubCfgDest, grubCfgContent, ct);
        
        // --- 6b. Write boot\grub\grub.cfg and boot\grub2\grub.cfg ---
        // Many distro GRUB binaries have embedded configurations that look for /boot/grub/grub.cfg or /boot/grub2/grub.cfg
        await File.WriteAllTextAsync(Path.Combine(bootGrubDir, "grub.cfg"), grubCfgContent, ct);
        await File.WriteAllTextAsync(Path.Combine(bootGrub2Dir, "grub.cfg"), grubCfgContent, ct);

        // Copy mmx64.efi (MokManager) if present in staging
        var mmxSource = Path.Combine(efiStagingDir, "mmx64.efi");
        if (File.Exists(mmxSource))
        {
            FileUtilities.SafeCopy(mmxSource, Path.Combine(efiBootDir, "mmx64.efi"));
            FileUtilities.SafeCopy(mmxSource, Path.Combine(efiVendorDir, "mmx64.efi"));
        }

        progress.Report(new ProgressUpdate(8, 9, "Populating EFI Environment", 78,
            $"[ESP Population] ✓ EFI\\{distro.EfiVendorDir}\\ written with vendor shim, GRUB, and grub.cfg"));

        // --- 7. Write win2linux\boot\vmlinuz and initrd ---
        var vmlinuzSrc = Path.Combine(bootStagingDir, "vmlinuz");
        var initrdSrc = Path.Combine(bootStagingDir, "initrd");
        var vmlinuzDest = Path.Combine(espBootDir, "vmlinuz");
        var initrdDest = Path.Combine(espBootDir, "initrd");

        FileUtilities.SafeMove(vmlinuzSrc, vmlinuzDest);
        progress.Report(new ProgressUpdate(8, 9, "Populating EFI Environment", 80,
            $"[ESP Population] ✓ vmlinuz copied ({new FileInfo(vmlinuzDest).Length / (1024 * 1024)} MB)"));

        // --- 7b. Inject win2linux.cpio overlay directly into initrd (PREPENDED) for Fedora ONLY ---
        // Prepending the uncompressed CPIO archive to the initramfs produces a standard early-CPIO
        // stream (identical to how CPU microcode is prepended) that the Linux kernel unpacks
        // natively BEFORE the main compressed initramfs for dracut pre-pivot hooks.
        // For Ubuntu and other non-Fedora distros, the stock initrd must remain pristine.
        string? cpioDest = null;
        if (distro.Id == "fedora")
        {
            var overlayBytes = await InitrdOverlayService.BuildOverlayArchiveBytesAsync(ct);
            await using (var fs = new FileStream(initrdDest, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                await fs.WriteAsync(overlayBytes, ct);
                await using (var srcFs = new FileStream(initrdSrc, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
                {
                    await srcFs.CopyToAsync(fs, ct);
                }
            }

            cpioDest = Path.Combine(espBootDir, "win2linux.cpio");
            await File.WriteAllBytesAsync(cpioDest, overlayBytes, ct);

            progress.Report(new ProgressUpdate(8, 9, "Populating EFI Environment", 83,
                $"[ESP Population] ✓ initrd populated with integrated prepended Win2Linux overlay ({new FileInfo(initrdDest).Length / (1024 * 1024)} MB)"));
        }
        else
        {
            FileUtilities.SafeMove(initrdSrc, initrdDest);
            progress.Report(new ProgressUpdate(8, 9, "Populating EFI Environment", 83,
                $"[ESP Population] ✓ Pristine initrd copied ({new FileInfo(initrdDest).Length / (1024 * 1024)} MB)"));
        }

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
            FileUtilities.SafeMove(squashfsSrc, squashfsDest);

            var sizeMb = new FileInfo(squashfsDest).Length / (1024 * 1024);
            progress.Report(new ProgressUpdate(8, 9, "Populating EFI Environment", 84,
                $"[ESP Population] ✓ LiveOS/squashfs.img ({sizeMb} MB) copied to ESP — dracut live root filesystem"));
        }

        // --- 8b. Copy Casper live filesystem and .disk info for Ubuntu/Debian live installers ---
        if (IsoExtractService.RequiresCasper(distro))
        {
            var casperSrcDir = Path.Combine(bootStagingDir, "casper");
            var casperEspDir = Path.Combine(espRoot, "casper");
            Directory.CreateDirectory(casperEspDir);

            if (Directory.Exists(casperSrcDir))
            {
                foreach (var file in Directory.GetFiles(casperSrcDir))
                {
                    var dest = Path.Combine(casperEspDir, Path.GetFileName(file));
                    FileUtilities.SafeMove(file, dest);
                }
            }

            // Also ensure vmlinuz and initrd are present in casper/ for native casper path lookups
            FileUtilities.SafeCopy(vmlinuzDest, Path.Combine(casperEspDir, "vmlinuz"));
            FileUtilities.SafeCopy(initrdDest, Path.Combine(casperEspDir, "initrd"));

            var diskSrcDir = Path.Combine(bootStagingDir, ".disk");
            var diskEspDir = Path.Combine(espRoot, ".disk");
            if (Directory.Exists(diskSrcDir))
            {
                Directory.CreateDirectory(diskEspDir);
                foreach (var file in Directory.GetFiles(diskSrcDir))
                {
                    FileUtilities.SafeMove(file, Path.Combine(diskEspDir, Path.GetFileName(file)));
                }
            }

            progress.Report(new ProgressUpdate(8, 9, "Populating EFI Environment", 84,
                $"[ESP Population] ✓ Casper live squashfs images and .disk info copied to ESP"));
        }

        // Ubuntu 26.04.1's Subiquity intentionally retains cdrom.sources for offline
        // installs. /cdrom is this ESP root, so preserve the suite index and package
        // pool from the original ISO at their original relative paths.
        if (IsoExtractService.RequiresOfflineAptRepository(distro))
        {
            if (!IsoExtractService.HasOfflineAptRepository(distro, bootStagingDir))
            {
                throw new EspPopulationException(
                    $"The staged {distro.DisplayName} APT repository is incomplete. " +
                    $"Expected dists/{distro.OfflineAptSuite}/Release and local packages under pool/. " +
                    "Refusing to create boot media that cannot satisfy Subiquity's offline APT source.");
            }

            foreach (var relativeDirectory in new[] { "dists", "pool" })
            {
                var sourceDirectory = Path.Combine(bootStagingDir, relativeDirectory);
                MoveDirectoryContents(sourceDirectory, Path.Combine(espRoot, relativeDirectory));
            }

            if (!IsoExtractService.HasOfflineAptRepository(distro, espRoot))
            {
                throw new EspPopulationException(
                    $"The {distro.DisplayName} offline APT repository did not survive ESP staging.");
            }
        }

        // --- 8c. Copy autoinstall.yaml / kickstart.ks ---
        var unattendedFilename = Win2Linux.Core.Orchestrator.InstallationOrchestrator.GetUnattendedFilename(distro.Id);
        var unattendedSrc = Path.Combine(unattendedDir, unattendedFilename);
        var unattendedDest = distro.Id == "ubuntu"
            ? Path.Combine(espRoot, unattendedFilename)
            : Path.Combine(espUnattendedDir, unattendedFilename);

        if (File.Exists(unattendedSrc))
        {
            FileUtilities.SafeCopy(unattendedSrc, unattendedDest);

            var metaDataSrc = Path.Combine(unattendedDir, "meta-data");
            if (unattendedFilename == "user-data" && File.Exists(metaDataSrc))
            {
                FileUtilities.SafeCopy(metaDataSrc, Path.Combine(espUnattendedDir, "meta-data"));
            }
        }
        else
        {
            throw new EspPopulationException(
                $"Unattended config not found at '{unattendedSrc}'. " +
                "Ensure staging generated the config before ESP population.");
        }

        // --- 9. Write meta-data for NoCloud-based installers ---
        // Ubuntu uses Subiquity's explicit autoinstallpath; Zorin still uses NoCloud.
        string? metaDataDest = null;
        if (unattendedFilename == "user-data")
        {
            metaDataDest = Path.Combine(espUnattendedDir, "meta-data");
            await File.WriteAllTextAsync(metaDataDest, "instance-id: win2linux-installer\n", ct);
        }

        progress.Report(new ProgressUpdate(8, 9, "Populating EFI Environment", 85,
            $"[ESP Population] ✓ Installer configuration payload written"));

        // --- 10. Verification of written files ---
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
        };

        if (metaDataDest != null)
        {
            requiredList.Add(metaDataDest);
        }

        if (cpioDest != null)
        {
            requiredList.Add(cpioDest);
        }

        // For Fedora/openSUSE: squashfs.img must also be on the ESP
        // (dracut rd.live.image mounts this as the root filesystem)
        if (squashfsDest != null)
        {
            requiredList.Add(squashfsDest);
        }

        if (IsoExtractService.RequiresCasper(distro))
        {
            var casperEspDir = Path.Combine(espRoot, "casper");
            if (!Directory.Exists(casperEspDir) || !Directory.GetFiles(casperEspDir, "*.squashfs").Any())
            {
                throw new EspPopulationException(
                    "Casper live filesystem verification failed: no .squashfs images found on the ESP. " +
                    "Without squashfs images, Casper halts with 'Unable to find a medium containing a live file system'.");
            }
        }

        foreach (var f in requiredList)
        {
            if (!File.Exists(f) || new FileInfo(f).Length == 0)
            {
                throw new EspPopulationException($"ESP population verification failed: '{f}' is missing or empty.");
            }
        }

        VerifyBootPayload(distro, espRoot);

        progress.Report(new ProgressUpdate(8, 9, "EFI Environment Complete", 87,
            $"[ESP Population] ✓ All {requiredList.Count} required files verified on ESP. Boot environment is ready."));
    }

    /// <summary>Check the final payload, including after temporary-file cleanup.</summary>
    public static void VerifyBootPayload(DistroProfile distro, string espRoot)
    {
        var required = new List<string>
        {
            "EFI/boot/bootx64.efi", "EFI/boot/grubx64.efi", "EFI/boot/grub.cfg",
            $"EFI/{distro.EfiVendorDir}/shimx64.efi", $"EFI/{distro.EfiVendorDir}/grubx64.efi",
            $"EFI/{distro.EfiVendorDir}/grub.cfg", "boot/grub/grub.cfg", "boot/grub2/grub.cfg",
            "win2linux/boot/vmlinuz", "win2linux/boot/initrd"
        };
        var configName = InstallationOrchestrator.GetUnattendedFilename(distro.Id);
        required.Add(distro.Id.Equals("ubuntu", StringComparison.OrdinalIgnoreCase)
            ? configName : $"win2linux/unattended/{configName}");
        if (configName == "user-data") required.Add("win2linux/unattended/meta-data");
        if (IsoExtractService.RequiresLiveSquashfs(distro)) required.Add("LiveOS/squashfs.img");
        if (IsoExtractService.RequiresCasper(distro))
        {
            required.AddRange(new[] { "casper/vmlinuz", "casper/initrd" });
            var casper = Path.Combine(espRoot, "casper");
            if (!Directory.Exists(casper) || !Directory.EnumerateFiles(casper, "*.squashfs")
                    .Any(f => new FileInfo(f).Length > 0))
                throw new EspPopulationException("Final ESP verification failed: Casper live filesystem is missing or empty.");
        }
        foreach (var relative in required)
        {
            var path = Path.Combine(espRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new EspPopulationException($"Final ESP verification failed: '{relative}' is missing or empty. Boot entry will not be registered.");
        }
        if (IsoExtractService.RequiresOfflineAptRepository(distro) &&
            !IsoExtractService.HasOfflineAptRepository(distro, espRoot))
        {
            throw new EspPopulationException(
                $"Final ESP verification failed: {distro.DisplayName} needs " +
                $"dists/{distro.OfflineAptSuite}/Release and local packages under pool/ for offline Subiquity APT.");
        }
    }

    private static void MoveDirectoryContents(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            FileUtilities.SafeMove(file, Path.Combine(destinationDirectory, relativePath));
        }
    }

    /// <summary>
    /// Generates a grub.cfg for the Linux installer environment.
    /// Uses GRUB's search --file to locate the ESP partition dynamically — no hardcoded partition numbers.
    /// </summary>
    public static string GenerateInstallerGrubConfig(DistroProfile distro, LinuxSetupConfiguration? setupConfig = null)
    {
        var extraArgs = string.Empty;
        if (setupConfig?.SafeGraphics == true)
        {
            extraArgs += " nomodeset";
        }
        if (setupConfig?.KernelSelection == "linux-7.3-legion")
        {
            extraArgs += " snd_hda_intel.model=dual-codecs";
        }
        if (!string.IsNullOrWhiteSpace(setupConfig?.CustomKernelArgs))
        {
            extraArgs += " " + setupConfig.CustomKernelArgs.Trim();
        }

        var autoinstallArgs = (distro.AutoinstallKernelArgs + extraArgs).Trim();
        var interactiveArgs = (distro.GetInteractiveKernelArgs() + extraArgs).Trim();
        var casperFallback = IsoExtractService.RequiresCasper(distro) ? """
                if search --no-floppy --file --set=win2linux_root /casper/vmlinuz; then
                    if [ -f ($win2linux_root)/casper/initrd ]; then
                        set root=$win2linux_root
                        set win2linux_bootdir=/casper
                        return 0
                    fi
                fi
            """ : string.Empty;

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
            insmod search_fs_file
            insmod test

            if [ x$grub_platform = xxen ]; then insmod xzio; insmod lzopio; fi

            function win2linux_find_boot {
                unset win2linux_root
                set win2linux_bootdir=/win2linux/boot
                if search --no-floppy --file --set=win2linux_root /win2linux/boot/vmlinuz; then
                    if [ -f ($win2linux_root)/win2linux/boot/initrd ]; then
                        set root=$win2linux_root
                        return 0
                    fi
                fi
            {{casperFallback}}
                echo "Win2Linux: kernel or initrd missing. Run the Windows boot repair before restarting."
                return 1
            }

            menuentry "Install {{distro.DisplayName}} (Automated Dual-Boot)" --class linux {
                echo "Win2Linux: Loading {{distro.DisplayName}} installer kernel (Automated)..."
                if win2linux_find_boot; then
                    if linux ($root)$win2linux_bootdir/vmlinuz {{autoinstallArgs}}; then
                        initrd ($root)$win2linux_bootdir/initrd
                    fi
                fi
            }

            menuentry "Try or Install {{distro.DisplayName}} (Interactive Live Desktop)" --class linux {
                echo "Win2Linux: Loading {{distro.DisplayName}} Live Desktop..."
                if win2linux_find_boot; then
                    if linux ($root)$win2linux_bootdir/vmlinuz {{interactiveArgs}}; then
                        initrd ($root)$win2linux_bootdir/initrd
                    fi
                fi
            }

            menuentry "Windows Boot Manager" --class windows {
                echo "Win2Linux: Chainloading Windows Boot Manager..."
                search --no-floppy --file --set=root /EFI/Microsoft/Boot/bootmgfw.efi
                chainloader ($root)/EFI/Microsoft/Boot/bootmgfw.efi
            }

            menuentry "UEFI Firmware Settings" {
                fwsetup
            }
            """;
    }


}

public class EspPopulationException(string message) : Exception(message);

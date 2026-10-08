using System.Diagnostics;
using System.IO;
using Win2Linux.Core.Common;
using Win2Linux.Core.Distros;
using Win2Linux.Core.Orchestrator;

namespace Win2Linux.Core.Extract;

/// <summary>
/// Extracts exactly the files needed from a Linux ISO:
/// - The installer kernel (vmlinuz) and initrd
/// - The EFI shim (BOOTX64.EFI) and GRUB EFI binary
/// - The LiveOS squashfs.img for distributions that use dracut live boot (Fedora, openSUSE)
///
/// Extraction strategy:
///   1. PRIMARY: Windows native Mount-DiskImage PowerShell cmdlet (native, fast, no external tools needed)
///   2. FALLBACK: WSL with 7-zip or bundled/installed full 7z.exe with ISO support
/// </summary>
public static class IsoExtractService
{
    /// <summary>
    /// Returns true if this distro requires a LiveOS squashfs to be present on the ESP
    /// for dracut's rd.live.image module to mount the root filesystem.
    /// Only openSUSE live ISOs use this mechanism — Fedora now uses the netinstall ISO
    /// which downloads packages from the internet and contains no squashfs image.
    /// </summary>
    public static bool RequiresLiveSquashfs(DistroProfile distro) =>
        distro.Id is "fedora";

    public static bool RequiresCasper(DistroProfile distro) =>
        distro.Id is "ubuntu" or "linuxmint" or "zorin";

    /// <summary>
    /// Ubuntu profiles with an OfflineAptSuite require the ISO's local APT index and
    /// package pool to be present at the root of /cdrom. Subiquity 26.04.1 keeps
    /// cdrom.sources enabled for offline installs and expects this full media layout.
    /// </summary>
    public static bool RequiresOfflineAptRepository(DistroProfile distro) =>
        distro.Id.Equals("ubuntu", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(distro.OfflineAptSuite);

    /// <summary>
    /// Copies an Ubuntu ISO's local APT repository into the boot staging root, where
    /// EspPopulationService places it at the root of the partition mounted as /cdrom.
    /// </summary>
    public static void StageOfflineAptRepositoryFromIsoRoot(
        DistroProfile distro,
        string isoRoot,
        string bootStagingDir,
        CancellationToken ct = default)
    {
        if (!RequiresOfflineAptRepository(distro))
            return;

        var suite = GetOfflineAptSuite(distro);
        var sourceRepository = Path.Combine(isoRoot, "dists", suite);
        var sourcePool = Path.Combine(isoRoot, "pool");
        if (!HasOfflineAptRepository(distro, isoRoot))
        {
            throw new IsoExtractException(
                $"The {distro.DisplayName} ISO is missing its offline APT repository: " +
                $"expected dists/{suite}/Release and at least one package under pool/. " +
                "The installer cannot use the partial boot payload as /cdrom.");
        }

        CopyDirectoryContents(sourceRepository, Path.Combine(bootStagingDir, "dists", suite), ct);
        CopyDirectoryContents(sourcePool, Path.Combine(bootStagingDir, "pool"), ct);

        if (!HasOfflineAptRepository(distro, bootStagingDir))
        {
            throw new IsoExtractException(
                $"The offline APT repository for {distro.DisplayName} was not staged completely.");
        }
    }

    /// <summary>Returns whether a repository root has the suite index and local packages.</summary>
    public static bool HasOfflineAptRepository(DistroProfile distro, string repositoryRoot)
    {
        if (!RequiresOfflineAptRepository(distro))
            return true;

        var releaseFile = Path.Combine(repositoryRoot, "dists", GetOfflineAptSuite(distro), "Release");
        var suiteDirectory = Path.GetDirectoryName(releaseFile)!;
        var poolDirectory = Path.Combine(repositoryRoot, "pool");
        return File.Exists(releaseFile) && new FileInfo(releaseFile).Length > 0 &&
               Directory.Exists(suiteDirectory) &&
               Directory.EnumerateFiles(suiteDirectory, "Packages*", SearchOption.AllDirectories)
                   .Any(f => new FileInfo(f).Length > 0) &&
               Directory.Exists(poolDirectory) &&
               Directory.EnumerateFiles(poolDirectory, "*.deb", SearchOption.AllDirectories).Any(f => new FileInfo(f).Length > 0);
    }

    /// <summary>
    /// Stages the official ISO repository used by Ubuntu 26.04.1's offline APT setup.
    /// The network is not used for this copy, and the repository is kept at the same
    /// relative paths as on the original ISO.
    /// </summary>
    public static async Task ExtractOfflineAptRepositoryAsync(
        DistroProfile distro,
        string isoPath,
        string bootStagingDir,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct = default)
    {
        if (!RequiresOfflineAptRepository(distro))
            return;

        progress.Report(new ProgressUpdate(5, 10, "Extracting Offline APT Repository", 46,
            $"[Extract] Staging dists/{GetOfflineAptSuite(distro)} and pool from the Ubuntu ISO..."));

        var extracted = false;
        char? driveLetter = null;
        try
        {
            driveLetter = await MountIsoAsync(isoPath, ct);
            if (driveLetter != null)
            {
                await Task.Run(
                    () => StageOfflineAptRepositoryFromIsoRoot(distro, $"{driveLetter.Value}:\\", bootStagingDir, ct),
                    ct);
                extracted = true;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            progress.Report(new ProgressUpdate(5, 10, "Extracting Offline APT Repository", 46,
                $"[Extract] Native ISO repository copy failed ({ex.Message}); trying the archive extractor..."));
        }
        finally
        {
            if (driveLetter != null)
                await DismountIsoAsync(isoPath, CancellationToken.None);
        }

        if (!extracted)
        {
            try
            {
                await ExtractOfflineAptRepositoryViaArchiveAsync(
                    isoPath, GetOfflineAptSuite(distro), bootStagingDir, CanUseWsl(), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new IsoExtractException(
                    $"Could not stage the offline APT repository from {Path.GetFileName(isoPath)}. " +
                    $"Ubuntu {distro.DisplayName} requires dists/{GetOfflineAptSuite(distro)}/Release and pool/ on /cdrom. " +
                    $"Archive extraction failed: {ex.Message}");
            }
        }

        if (!HasOfflineAptRepository(distro, bootStagingDir))
        {
            throw new IsoExtractException(
                $"The staged Ubuntu APT repository is incomplete: dists/{GetOfflineAptSuite(distro)}/Release " +
                "or local pool packages are missing.");
        }

        progress.Report(new ProgressUpdate(5, 10, "Offline APT Repository Staged", 47,
            "[Extract] ✓ Ubuntu suite metadata and package pool are available locally for Subiquity"));
    }

    private static string GetOfflineAptSuite(DistroProfile distro)
    {
        var suite = distro.OfflineAptSuite?.Trim();
        if (string.IsNullOrEmpty(suite) || suite.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '.' and not '+'))
            throw new IsoExtractException($"Ubuntu profile '{distro.DisplayName}' has an invalid offline APT suite.");
        return suite;
    }

    private static void CopyDirectoryContents(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            FileUtilities.SafeCopy(file, Path.Combine(destinationDirectory, relativePath));
        }
    }

    private static async Task ExtractOfflineAptRepositoryViaArchiveAsync(
        string isoPath,
        string suite,
        string destinationDirectory,
        bool useWsl,
        CancellationToken ct)
    {
        Directory.CreateDirectory(destinationDirectory);
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (useWsl)
        {
            psi.FileName = "wsl.exe";
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("7z");
            psi.ArgumentList.Add("x");
            psi.ArgumentList.Add("-tiso");
            psi.ArgumentList.Add(ToWslPath(isoPath));
            psi.ArgumentList.Add($"dists/{suite}/*");
            psi.ArgumentList.Add("pool/*");
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add($"-o{ToWslPath(destinationDirectory)}");
            psi.ArgumentList.Add("-y");
        }
        else
        {
            psi.FileName = ResolveSevenZipExe()
                ?? throw new IsoExtractException("ISO-capable 7z.exe not found and WSL 7-zip is unavailable.");
            psi.ArgumentList.Add("x");
            psi.ArgumentList.Add("-tiso");
            psi.ArgumentList.Add(isoPath);
            psi.ArgumentList.Add($"dists/{suite}/*");
            psi.ArgumentList.Add("pool/*");
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add($"-o{destinationDirectory}");
            psi.ArgumentList.Add("-y");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var proc = Process.Start(psi)
            ?? throw new IsoExtractException("Failed to start the ISO repository archive extractor.");
        var stdoutDrain = proc.StandardOutput.BaseStream.CopyToAsync(Stream.Null, linked.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await proc.WaitForExitAsync(linked.Token);
            await stdoutDrain;
            var stderr = await stderrTask;
            if (proc.ExitCode != 0)
                throw new IsoExtractException($"7-zip exited with code {proc.ExitCode}: {stderr}");
        }
        catch (OperationCanceledException)
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
            throw;
        }
    }

    /// <summary>
    /// Extracts all required boot files from the ISO to the staging directories.
    /// </summary>
    /// <param name="distro">The selected distribution profile.</param>
    /// <param name="isoPath">Path to the downloaded and verified ISO.</param>
    /// <param name="bootStagingDir">Where to write vmlinuz, initrd, and squashfs.img (e.g. D:\win2linux\boot\)</param>
    /// <param name="efiStagingDir">Where to write EFI binaries for later ESP population (e.g. D:\win2linux\efi-stage\)</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ExtractBootFilesAsync(
        DistroProfile distro,
        string isoPath,
        string bootStagingDir,
        string efiStagingDir,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(bootStagingDir);
        Directory.CreateDirectory(efiStagingDir);
        FileUtilities.RemoveReadOnlyRecursive(bootStagingDir);
        FileUtilities.RemoveReadOnlyRecursive(efiStagingDir);

        progress.Report(new ProgressUpdate(5, 10, "Extracting Boot Files", 36,
            $"[Extract] Extracting kernel, initrd, and EFI binaries from {Path.GetFileName(isoPath)}..."));

        bool extracted = false;
        string extractionBackend = "Native Windows Mount";

        // --- BACKEND 1 (PRIMARY): Windows native Mount-DiskImage ---
        try
        {
            extracted = await TryExtractViaMountAsync(distro, isoPath, bootStagingDir, efiStagingDir, progress, ct);
        }
        catch (Exception ex)
        {
            progress.Report(new ProgressUpdate(5, 10, "Extracting Boot Files", 37,
                $"[Extract] Native mount attempt encountered: {ex.Message}. Trying fallback extractor..."));
        }

        // --- BACKEND 2 (FALLBACK): WSL or 7z archive extraction ---
        if (!extracted)
        {
            bool useWsl = CanUseWsl();
            extractionBackend = useWsl ? "WSL 7-zip" : "Windows 7-Zip";
            await ExtractViaArchiveAsync(distro, isoPath, bootStagingDir, efiStagingDir, useWsl, progress, ct);
        }

        // Upgrade to SBAT-compliant (grub,5 / shim,4) official signed binaries if available
        ApplySbatUpdatesIfAvailable(distro, efiStagingDir, progress);

        // Ensure shimx64.efi exists in the staging dir (used for vendor EFI directory)
        var bootX64Path = Path.Combine(efiStagingDir, "BOOTX64.EFI");
        var shimCopyPath = Path.Combine(efiStagingDir, "shimx64.efi");
        if (File.Exists(bootX64Path))
        {
            FileUtilities.SafeCopy(bootX64Path, shimCopyPath);
        }

        // --- Final verification ---
        var required = new[]
        {
            Path.Combine(bootStagingDir, "vmlinuz"),
            Path.Combine(bootStagingDir, "initrd"),
            bootX64Path,
            Path.Combine(efiStagingDir, "grubx64.efi"),
            shimCopyPath,
        };

        foreach (var f in required)
        {
            if (!File.Exists(f) || new FileInfo(f).Length == 0)
            {
                throw new IsoExtractException(
                    $"Extraction verification failed: '{f}' is missing or empty after ISO extraction.");
            }
        }

        progress.Report(new ProgressUpdate(5, 10, "Boot Files Extracted", 45,
            $"[Extract] All boot files verified successfully. Backend: {extractionBackend}"));
    }

    /// <summary>
    /// Extracts the LiveOS squashfs root image from a Fedora/openSUSE live ISO.
    /// This is required because dracut's rd.live.image module must be able to mount
    /// a SquashFS filesystem image as the root device. Without it, dracut panics with
    /// "dracut-mount: Can't mount root filesystem" and drops to an emergency shell.
    ///
    /// The squashfs is placed at: {bootStagingDir}\LiveOS\squashfs.img
    /// EspPopulationService then copies it to:  ESP:\win2linux\boot\LiveOS\squashfs.img
    /// The kernel arg root=live:CDLABEL=LINUXEFI tells dracut to look for
    /// /LiveOS/squashfs.img on the partition with label LINUXEFI (our ESP).
    /// </summary>
    public static async Task ExtractLiveSquashfsAsync(
        string isoPath,
        string bootStagingDir,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct = default)
    {
        var squashfsDest = Path.Combine(bootStagingDir, "LiveOS", "squashfs.img");
        Directory.CreateDirectory(Path.GetDirectoryName(squashfsDest)!);

        progress.Report(new ProgressUpdate(5, 10, "Extracting Boot Files", 44,
            "[Extract] Extracting LiveOS/squashfs.img (root filesystem for live boot)..."));

        // Fedora: LiveOS/squashfs.img; openSUSE: LiveOS/squashfs.img (same path)
        var candidatePaths = new[]
        {
            "LiveOS/squashfs.img",
            "liveos/squashfs.img",
        };

        bool useWsl = CanUseWsl();
        bool extracted = false;

        // Try native mount first (much faster — avoids extracting a multi-GB squashfs via 7z)
        try
        {
            var driveLetter = await MountIsoAsync(isoPath, ct);
            if (driveLetter != null)
            {
                try
                {
                    foreach (var candidate in candidatePaths)
                    {
                        var normalizedCandidate = candidate.Replace('/', '\\');
                        var src = Path.Combine($"{driveLetter}:\\", normalizedCandidate);
                        if (File.Exists(src) && new FileInfo(src).Length > 0)
                        {
                            FileUtilities.SafeCopy(src, squashfsDest);
                            extracted = true;
                            break;
                        }
                    }
                }
                finally
                {
                    await DismountIsoAsync(isoPath, ct);
                }
            }
        }
        catch (Exception ex)
        {
            progress.Report(new ProgressUpdate(5, 10, "Extracting Boot Files", 44,
                $"[Extract] Native squashfs mount failed ({ex.Message}), trying archive extraction..."));
        }

        // Archive fallback
        if (!extracted)
        {
            foreach (var candidate in candidatePaths)
            {
                try
                {
                    await ExtractSingleFileAsync(isoPath, candidate,
                        squashfsDest, useWsl, ct);
                    if (File.Exists(squashfsDest) && new FileInfo(squashfsDest).Length > 0)
                    {
                        extracted = true;
                        break;
                    }
                }
                catch { /* try next candidate */ }
            }
        }

        if (!extracted || !File.Exists(squashfsDest) || new FileInfo(squashfsDest).Length == 0)
        {
            throw new IsoExtractException(
                "Could not extract LiveOS/squashfs.img from the ISO. " +
                "This file is required for the Fedora/openSUSE live installer to boot. " +
                "Ensure the ISO is a valid live image (not a netinstall).");
        }

        var sizeMb = new FileInfo(squashfsDest).Length / (1024 * 1024);
        progress.Report(new ProgressUpdate(5, 10, "Extracting Boot Files", 45,
            $"[Extract] ✓ LiveOS/squashfs.img extracted ({sizeMb} MB) — dracut live root filesystem ready"));
    }

    /// <summary>
    /// Extracts Casper live filesystem images (*.squashfs) and release info (.disk/info)
    /// for Ubuntu, Linux Mint, and Zorin OS live installers.
    /// Without these files present on the ESP, Casper halts with
    /// "Unable to find a medium containing a live file system".
    /// </summary>
    public static async Task ExtractCasperFilesAsync(
        string isoPath,
        string bootStagingDir,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct = default)
    {
        var casperDestDir = Path.Combine(bootStagingDir, "casper");
        var diskDestDir = Path.Combine(bootStagingDir, ".disk");
        Directory.CreateDirectory(casperDestDir);
        Directory.CreateDirectory(diskDestDir);

        progress.Report(new ProgressUpdate(5, 10, "Extracting Casper Live Files", 44,
            "[Extract] Extracting Casper squashfs filesystem images and release info..."));

        char? driveLetter = null;
        try
        {
            driveLetter = await MountIsoAsync(isoPath, ct);
            if (driveLetter != null)
            {
                var isoRoot = $"{driveLetter.Value}:\\";
                var isoCasper = Path.Combine(isoRoot, "casper");
                if (Directory.Exists(isoCasper))
                {
                    foreach (var file in Directory.GetFiles(isoCasper, "*.*"))
                    {
                        var fileName = Path.GetFileName(file);
                        if (fileName.EndsWith(".squashfs", StringComparison.OrdinalIgnoreCase) ||
                            fileName.EndsWith(".size", StringComparison.OrdinalIgnoreCase) ||
                            fileName.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase) ||
                            fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                        {
                            var destFile = Path.Combine(casperDestDir, fileName);
                            FileUtilities.SafeCopy(file, destFile);
                        }
                    }
                }

                var isoDisk = Path.Combine(isoRoot, ".disk");
                if (Directory.Exists(isoDisk))
                {
                    foreach (var file in Directory.GetFiles(isoDisk, "*.*"))
                    {
                        FileUtilities.SafeCopy(file, Path.Combine(diskDestDir, Path.GetFileName(file)));
                    }
                }
            }
        }
        finally
        {
            if (driveLetter != null)
            {
                await DismountIsoAsync(isoPath, ct);
            }
        }

        // Verify that at least one root filesystem squashfs image was extracted
        var squashfsCount = Directory.GetFiles(casperDestDir, "*.squashfs").Length;
        if (squashfsCount == 0)
        {
            throw new IsoExtractException(
                "Could not extract any Casper squashfs root filesystem images from the ISO. " +
                "Without these files, Casper halts with 'Unable to find a medium containing a live file system'. " +
                "Ensure the ISO is a valid desktop/server live image containing a /casper directory.");
        }

        progress.Report(new ProgressUpdate(5, 10, "Casper Files Extracted", 45,
            $"[Extract] ✓ Casper live filesystem ({squashfsCount} squashfs image(s)) and .disk info staged for ESP population"));
    }

    private static async Task<bool> TryExtractViaMountAsync(
        DistroProfile distro,
        string isoPath,
        string bootStagingDir,
        string efiStagingDir,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct)
    {
        char? driveLetter = null;
        try
        {
            driveLetter = await MountIsoAsync(isoPath, ct);
            if (driveLetter == null)
            {
                return false;
            }

            string root = $"{driveLetter.Value}:\\";

            // 1. Kernel
            var kernelFound = TryCopyFileFromCandidates(root, distro.IsoKernelPath, new[]
            {
                "casper\\vmlinuz",
                "isolinux\\vmlinuz",
                "images\\pxeboot\\vmlinuz",
                "boot\\x86_64\\loader\\linux",
                "install.amd\\vmlinuz"
            }, Path.Combine(bootStagingDir, "vmlinuz"));

            if (!kernelFound)
            {
                throw new FileNotFoundException($"Could not locate kernel on mounted ISO at {root}");
            }

            progress.Report(new ProgressUpdate(5, 10, "Extracting Boot Files", 38,
                $"[Extract] ✓ vmlinuz extracted ({GetFileSizeKb(Path.Combine(bootStagingDir, "vmlinuz"))} KB)"));

            // 2. Initrd
            var initrdFound = TryCopyFileFromCandidates(root, distro.IsoInitrdPath, new[]
            {
                "casper\\initrd",
                "casper\\initrd.lz",
                "isolinux\\initrd.img",
                "images\\pxeboot\\initrd.img",
                "boot\\x86_64\\loader\\initrd",
                "install.amd\\initrd.gz"
            }, Path.Combine(bootStagingDir, "initrd"));

            if (!initrdFound)
            {
                throw new FileNotFoundException($"Could not locate initrd on mounted ISO at {root}");
            }

            progress.Report(new ProgressUpdate(5, 10, "Extracting Boot Files", 40,
                $"[Extract] ✓ initrd extracted ({GetFileSizeKb(Path.Combine(bootStagingDir, "initrd"))} KB)"));

            // 3. BOOTX64.EFI
            var bootx64Found = TryCopyFileFromCandidates(root, distro.IsoShimPath, new[]
            {
                "EFI\\boot\\bootx64.efi",
                "EFI\\BOOT\\BOOTX64.EFI",
                "EFI\\BOOT\\bootx64.efi"
            }, Path.Combine(efiStagingDir, "BOOTX64.EFI"));

            if (!bootx64Found)
            {
                throw new FileNotFoundException($"Could not locate BOOTX64.EFI on mounted ISO at {root}");
            }

            progress.Report(new ProgressUpdate(5, 10, "Extracting Boot Files", 42,
                $"[Extract] ✓ BOOTX64.EFI extracted ({GetFileSizeKb(Path.Combine(efiStagingDir, "BOOTX64.EFI"))} KB)"));

            // 4. grubx64.efi
            var grubFound = TryCopyFileFromCandidates(root, distro.IsoGrubEfiPath, new[]
            {
                "EFI\\boot\\grubx64.efi",
                "EFI\\BOOT\\grubx64.efi",
                "EFI\\ubuntu\\grubx64.efi",
                "EFI\\fedora\\grubx64.efi",
                "EFI\\debian\\grubx64.efi",
                "EFI\\opensuse\\grubx64.efi"
            }, Path.Combine(efiStagingDir, "grubx64.efi"));

            if (!grubFound)
            {
                throw new FileNotFoundException($"Could not locate grubx64.efi on mounted ISO at {root}");
            }

            progress.Report(new ProgressUpdate(5, 10, "Extracting Boot Files", 44,
                $"[Extract] ✓ grubx64.efi extracted ({GetFileSizeKb(Path.Combine(efiStagingDir, "grubx64.efi"))} KB)"));

            return true;
        }
        finally
        {
            if (driveLetter != null)
            {
                await DismountIsoAsync(isoPath, ct);
            }
        }
    }

    private static bool TryCopyFileFromCandidates(string root, string primaryRelative, string[] fallbackRelatives, string destPath)
    {
        var candidates = new List<string> { primaryRelative };
        candidates.AddRange(fallbackRelatives);

        foreach (var rel in candidates)
        {
            var normalizedRel = rel.Replace('/', '\\').TrimStart('\\');
            var fullPath = Path.Combine(root, normalizedRel);
            if (File.Exists(fullPath) && new FileInfo(fullPath).Length > 0)
            {
                FileUtilities.SafeCopy(fullPath, destPath);
                return true;
            }
        }

        return false;
    }

    private static async Task<char?> MountIsoAsync(string isoPath, CancellationToken ct)
    {
        await DismountIsoAsync(isoPath, ct);

        // Mount-DiskImage via PowerShell and extract the assigned drive letter
        var script = $"Mount-DiskImage -ImagePath '{isoPath}' -StorageType ISO -PassThru | ForEach-Object {{ ($_ | Get-Volume).DriveLetter }}";
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
        using var proc = Process.Start(psi);
        if (proc == null) return null;

        var stdout = await proc.StandardOutput.ReadToEndAsync(linked.Token);
        await proc.WaitForExitAsync(linked.Token);

        if (proc.ExitCode == 0)
        {
            var trimmed = stdout.Trim();
            if (!string.IsNullOrEmpty(trimmed) && char.IsLetter(trimmed[0]))
            {
                return char.ToUpperInvariant(trimmed[0]);
            }
        }

        return null;
    }

    private static async Task DismountIsoAsync(string isoPath, CancellationToken ct)
    {
        try
        {
            var script = $"Dismount-DiskImage -ImagePath '{isoPath}' -ErrorAction SilentlyContinue | Out-Null";
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                await proc.WaitForExitAsync(ct);
            }
        }
        catch
        {
            // Non-fatal cleanup
        }
    }

    private static async Task ExtractViaArchiveAsync(
        DistroProfile distro,
        string isoPath,
        string bootStagingDir,
        string efiStagingDir,
        bool useWsl,
        IProgress<ProgressUpdate> progress,
        CancellationToken ct)
    {
        // Extract kernel
        await ExtractSingleFileWithFallbackAsync(
            isoPath,
            distro.IsoKernelPath,
            new[] { "images/pxeboot/vmlinuz", "boot/x86_64/loader/linux", "install.amd/vmlinuz", "casper/vmlinuz" },
            Path.Combine(bootStagingDir, "vmlinuz"),
            useWsl, ct);

        progress.Report(new ProgressUpdate(5, 10, "Extracting Boot Files", 38,
            $"[Extract] ✓ vmlinuz extracted ({GetFileSizeKb(Path.Combine(bootStagingDir, "vmlinuz"))} KB)"));

        // Extract initrd
        await ExtractSingleFileWithFallbackAsync(
            isoPath,
            distro.IsoInitrdPath,
            new[] { "images/pxeboot/initrd.img", "boot/x86_64/loader/initrd", "install.amd/initrd.gz", "casper/initrd" },
            Path.Combine(bootStagingDir, "initrd"),
            useWsl, ct);

        progress.Report(new ProgressUpdate(5, 10, "Extracting Boot Files", 40,
            $"[Extract] ✓ initrd extracted ({GetFileSizeKb(Path.Combine(bootStagingDir, "initrd"))} KB)"));

        // Extract BOOTX64.EFI
        var bootX64Path = Path.Combine(efiStagingDir, "BOOTX64.EFI");
        await ExtractSingleFileWithFallbackAsync(
            isoPath,
            distro.IsoShimPath,
            new[] { "EFI/BOOT/BOOTX64.EFI", "EFI/BOOT/bootx64.efi", "EFI/boot/BOOTX64.EFI", "efi/boot/bootx64.efi" },
            bootX64Path,
            useWsl, ct);

        progress.Report(new ProgressUpdate(5, 10, "Extracting Boot Files", 42,
            $"[Extract] ✓ BOOTX64.EFI extracted ({GetFileSizeKb(bootX64Path)} KB)"));

        // Extract grubx64.efi
        var grubEfiPath = Path.Combine(efiStagingDir, "grubx64.efi");
        await ExtractSingleFileWithFallbackAsync(
            isoPath,
            distro.IsoGrubEfiPath,
            new[] { "EFI/BOOT/grubx64.efi", "EFI/BOOT/GRUBX64.EFI", "efi/boot/grubx64.efi" },
            grubEfiPath,
            useWsl, ct);

        progress.Report(new ProgressUpdate(5, 10, "Extracting Boot Files", 44,
            $"[Extract] ✓ grubx64.efi extracted ({GetFileSizeKb(grubEfiPath)} KB)"));
    }

    private static async Task ExtractSingleFileAsync(
        string isoPath,
        string isoInternalPath,
        string destinationPath,
        bool useWsl,
        CancellationToken ct)
    {
        var normalizedIsoPath = isoInternalPath.Replace('\\', '/');
        var destDir = Path.GetDirectoryName(destinationPath)!;
        var tempDestName = Path.GetFileName(normalizedIsoPath);

        Directory.CreateDirectory(destDir);

        if (useWsl)
        {
            await RunWslExtractAsync(isoPath, normalizedIsoPath, destDir, ct);
        }
        else
        {
            await RunSevenZipExtractAsync(isoPath, normalizedIsoPath, destDir, ct);
        }

        var extractedPath = Path.Combine(destDir, tempDestName);
        if (File.Exists(extractedPath))
        {
            if (extractedPath != destinationPath)
            {
                FileUtilities.SafeCopy(extractedPath, destinationPath);
                try { File.Delete(extractedPath); } catch { }
            }
            else
            {
                FileUtilities.SafeCopy(extractedPath, destinationPath);
            }
        }

        if (!File.Exists(destinationPath) || new FileInfo(destinationPath).Length == 0)
        {
            throw new FileNotFoundException($"File '{isoInternalPath}' was not extracted to '{destinationPath}'.");
        }
    }

    private static async Task ExtractSingleFileWithFallbackAsync(
        string isoPath,
        string primaryInternalPath,
        string[] fallbackPaths,
        string destinationPath,
        bool useWsl,
        CancellationToken ct)
    {
        var allPaths = new List<string> { primaryInternalPath };
        allPaths.AddRange(fallbackPaths);

        Exception? lastEx = null;
        foreach (var path in allPaths)
        {
            try
            {
                await ExtractSingleFileAsync(isoPath, path, destinationPath, useWsl, ct);
                if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
        }

        throw lastEx ?? new IsoExtractException($"Could not extract {destinationPath} from any candidate paths in {isoPath}");
    }

    private static async Task RunSevenZipExtractAsync(
        string isoPath, string isoInternalPath, string destDir, CancellationToken ct)
    {
        var sevenZipExe = ResolveSevenZipExe()
            ?? throw new IsoExtractException(
                "ISO-capable 7z.exe not found in application tools or the standard 7-Zip installation directory. " +
                "Use full 7-Zip (7z.exe and 7z.dll) or WSL with 7z; standalone 7za.exe cannot read ISO media.");

        var psi = new ProcessStartInfo
        {
            FileName = sevenZipExe,
            Arguments = $"e -tiso \"{isoPath}\" -o\"{destDir}\" \"{isoInternalPath}\" -y",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
        using var proc = Process.Start(psi) ?? throw new IsoExtractException("Failed to start 7z.exe");

        await proc.WaitForExitAsync(linked.Token);
        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync(linked.Token);
            throw new IsoExtractException($"7z.exe exited with code {proc.ExitCode}: {err}");
        }
    }

    private static async Task RunWslExtractAsync(
        string isoPath, string isoInternalPath, string destDir, CancellationToken ct)
    {
        var wslIsoPath = ToWslPath(isoPath);
        var wslDestDir = ToWslPath(destDir);

        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            Arguments = $"-- 7z e -tiso \"{wslIsoPath}\" -o\"{wslDestDir}\" \"{isoInternalPath}\" -y",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
        using var proc = Process.Start(psi) ?? throw new IsoExtractException("Failed to start wsl.exe for extraction");

        await proc.WaitForExitAsync(linked.Token);
        if (proc.ExitCode > 1)
        {
            var err = await proc.StandardError.ReadToEndAsync(linked.Token);
            throw new IsoExtractException($"WSL 7z exited with code {proc.ExitCode}: {err}");
        }
    }

    private static bool CanUseWsl()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = "-- which 7z",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string ToWslPath(string windowsPath)
    {
        if (windowsPath.Length >= 2 && windowsPath[1] == ':')
        {
            var driveLetter = windowsPath[0].ToString().ToLowerInvariant();
            var rest = windowsPath[2..].Replace('\\', '/');
            return $"/mnt/{driveLetter}{rest}";
        }
        return windowsPath.Replace('\\', '/');
    }

    private static string? ResolveSevenZipExe()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "7z.exe"),
            Path.Combine(AppContext.BaseDirectory, "7z.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools", "7z.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "Win2Linux.UI", "tools", "7z.exe"),
            Path.Combine(Environment.CurrentDirectory, "src", "Win2Linux.UI", "tools", "7z.exe"),
            Path.Combine(Environment.CurrentDirectory, "tools", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
        };

        foreach (var c in candidates)
        {
            try
            {
                var full = Path.GetFullPath(c);
                if (File.Exists(full)) return full;
            }
            catch { }
        }

        return null;
    }

    private static long GetFileSizeKb(string path)
    {
        try { return new FileInfo(path).Length / 1024; }
        catch { return 0; }
    }

    private static void ApplySbatUpdatesIfAvailable(DistroProfile distro, string efiStagingDir, IProgress<ProgressUpdate> progress)
    {
        var sbatFolder = distro.Id.ToLowerInvariant();
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "sbat", sbatFolder),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools", "sbat", sbatFolder),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "Win2Linux.UI", "tools", "sbat", sbatFolder),
            Path.Combine(Environment.CurrentDirectory, "src", "Win2Linux.UI", "tools", "sbat", sbatFolder)
        };

        if (distro.Id is "linuxmint" or "zorin")
        {
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "tools", "sbat", "ubuntu"));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools", "sbat", "ubuntu"));
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "Win2Linux.UI", "tools", "sbat", "ubuntu"));
            candidates.Add(Path.Combine(Environment.CurrentDirectory, "src", "Win2Linux.UI", "tools", "sbat", "ubuntu"));
        }

        foreach (var dir in candidates)
        {
            try
            {
                var fullDir = Path.GetFullPath(dir);
                var grubSrc = Path.Combine(fullDir, "grubx64.efi");
                var shimSrc = Path.Combine(fullDir, "BOOTX64.EFI");
                if (File.Exists(grubSrc) && File.Exists(shimSrc))
                {
                    FileUtilities.SafeCopy(grubSrc, Path.Combine(efiStagingDir, "grubx64.efi"));
                    FileUtilities.SafeCopy(shimSrc, Path.Combine(efiStagingDir, "BOOTX64.EFI"));
                    FileUtilities.SafeCopy(shimSrc, Path.Combine(efiStagingDir, "shimx64.efi"));

                    var mmxSrc = Path.Combine(fullDir, "mmx64.efi");
                    if (File.Exists(mmxSrc))
                    {
                        FileUtilities.SafeCopy(mmxSrc, Path.Combine(efiStagingDir, "mmx64.efi"));
                    }

                    progress.Report(new ProgressUpdate(5, 10, "Boot Files Extracted", 44,
                        $"[Extract] ✓ Upgraded to official SBAT-compliant (grub,5 + shim,4) bootloaders for Secure Boot"));
                    return;
                }
            }
            catch { }
        }
    }
}

public class IsoExtractException(string message) : Exception(message);

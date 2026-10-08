using Win2Linux.Core.Boot;
using Win2Linux.Core.Distros;
using Win2Linux.Core.Extract;
using Win2Linux.Core.Orchestrator;

namespace Win2Linux.Tests;

public sealed class BootPayloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Win2Linux-BootTests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("ubuntu")]
    [InlineData("fedora")]
    [InlineData("linuxmint")]
    [InlineData("zorin")]
    public async Task CleanupPreservesFinalKernelInitrdAndInstallerConfig(string distroId)
    {
        var distro = DistroRegistry.SupportedDistributions.Single(d => d.Id == distroId);
        var staging = new EspStagingLayout(_root);
        Write(staging.BootDirectory, "vmlinuz", "test-kernel");
        Write(staging.BootDirectory, "initrd", "test-initrd");
        Write(staging.EfiDirectory, "BOOTX64.EFI", "test-shim");
        Write(staging.EfiDirectory, "shimx64.efi", "test-shim");
        Write(staging.EfiDirectory, "grubx64.efi", "test-grub");
        Write(staging.UnattendedDirectory, InstallationOrchestrator.GetUnattendedFilename(distroId), "test-config");
        if (distroId == "fedora") Write(staging.BootDirectory, "LiveOS/squashfs.img", "test-rootfs");
        else Write(staging.BootDirectory, "casper/test.squashfs", "test-rootfs");
        if (IsoExtractService.RequiresOfflineAptRepository(distro))
        {
            Write(staging.BootDirectory, $"dists/{distro.OfflineAptSuite}/Release", "Suite: resolute\n");
            Write(staging.BootDirectory, "dists/resolute/main/binary-amd64/Packages.gz", "package index");
            Write(staging.BootDirectory, "pool/main/test-package_1.0_amd64.deb", "package bytes");
        }

        await EspPopulationService.PopulateEspAsync(distro, _root, staging.BootDirectory,
            staging.EfiDirectory, staging.UnattendedDirectory, new Progress<ProgressUpdate>());
        staging.Cleanup();

        Assert.False(Directory.Exists(staging.Root));
        EspPopulationService.VerifyBootPayload(distro, _root);
        Assert.Equal("test-kernel", File.ReadAllText(Path.Combine(_root, "win2linux", "boot", "vmlinuz")));
        if (IsoExtractService.RequiresOfflineAptRepository(distro))
        {
            Assert.Equal("Suite: resolute\n", File.ReadAllText(Path.Combine(_root, "dists", "resolute", "Release")));
            Assert.Equal("package bytes", File.ReadAllText(Path.Combine(_root, "pool", "main", "test-package_1.0_amd64.deb")));
            Assert.True(IsoExtractService.HasOfflineAptRepository(distro, _root));
        }
        var initrd = File.ReadAllBytes(Path.Combine(_root, "win2linux", "boot", "initrd"));
        if (distroId == "fedora") Assert.EndsWith("test-initrd", System.Text.Encoding.UTF8.GetString(initrd));
        else Assert.Equal("test-initrd", System.Text.Encoding.UTF8.GetString(initrd));

        // Catch the original failure even when /casper still contains a copy.
        File.Delete(Path.Combine(_root, "win2linux", "boot", "vmlinuz"));
        var error = Assert.Throws<EspPopulationException>(() => EspPopulationService.VerifyBootPayload(distro, _root));
        Assert.Contains("win2linux/boot/vmlinuz", error.Message);
    }

    [Fact]
    public void GrubChecksSearchResultAndUsesMatchingKernelAndInitrdDirectory()
    {
        var ubuntu = DistroRegistry.SupportedDistributions.Single(d => d.Id == "ubuntu");
        var config = EspPopulationService.GenerateInstallerGrubConfig(ubuntu);
        Assert.Contains("unset win2linux_root", config);
        Assert.Contains("if search --no-floppy --file --set=win2linux_root /win2linux/boot/vmlinuz; then", config);
        Assert.Contains("if [ -f ($win2linux_root)/win2linux/boot/initrd ]; then", config);
        Assert.Contains("set win2linux_bootdir=/casper", config);
        Assert.Contains("if linux ($root)$win2linux_bootdir/vmlinuz", config);
        Assert.Contains("initrd ($root)$win2linux_bootdir/initrd", config);
        Assert.DoesNotContain("if [ -z \"$root\" ]", config);
        Assert.DoesNotContain("--label --set=root LINUXEFI", config);
    }

    [Fact]
    public void EmptyFinalKernelIsRejected()
    {
        var ubuntu = DistroRegistry.SupportedDistributions.Single(d => d.Id == "ubuntu");
        foreach (var relative in new[] { "EFI/boot/bootx64.efi", "EFI/boot/grubx64.efi", "EFI/boot/grub.cfg",
                     "EFI/ubuntu/shimx64.efi", "EFI/ubuntu/grubx64.efi", "EFI/ubuntu/grub.cfg",
                     "boot/grub/grub.cfg", "boot/grub2/grub.cfg", "win2linux/boot/vmlinuz",
                     "win2linux/boot/initrd", "autoinstall.yaml", "casper/vmlinuz", "casper/initrd", "casper/root.squashfs" })
            Write(_root, relative, relative.EndsWith("boot/vmlinuz") ? "" : "test");
        var error = Assert.Throws<EspPopulationException>(() => EspPopulationService.VerifyBootPayload(ubuntu, _root));
        Assert.Contains("win2linux/boot/vmlinuz", error.Message);
    }

    [Fact]
    public void Ubuntu26OfflineAptRepositoryCopiesSuiteMetadataAndPackagePool()
    {
        var ubuntu = DistroRegistry.SupportedDistributions.Single(d => d.Id == "ubuntu");
        Assert.Equal("resolute", ubuntu.OfflineAptSuite);
        Assert.True(IsoExtractService.RequiresOfflineAptRepository(ubuntu));

        var isoRoot = Path.Combine(_root, "iso");
        var stagingRoot = Path.Combine(_root, "staging");
        Write(isoRoot, "dists/resolute/Release", "Suite: resolute\n");
        Write(isoRoot, "dists/resolute/main/binary-amd64/Packages.gz", "package index");
        Write(isoRoot, "pool/main/test-package_1.0_amd64.deb", "package bytes");

        IsoExtractService.StageOfflineAptRepositoryFromIsoRoot(ubuntu, isoRoot, stagingRoot);

        Assert.True(IsoExtractService.HasOfflineAptRepository(ubuntu, stagingRoot));
        Assert.Equal("package index", File.ReadAllText(Path.Combine(stagingRoot, "dists", "resolute", "main", "binary-amd64", "Packages.gz")));
        Assert.Equal("package bytes", File.ReadAllText(Path.Combine(stagingRoot, "pool", "main", "test-package_1.0_amd64.deb")));
    }

    [Fact]
    public void UbuntuOfflineAptRepositoryRejectsMissingReleaseMetadata()
    {
        var ubuntu = DistroRegistry.SupportedDistributions.Single(d => d.Id == "ubuntu");
        var isoRoot = Path.Combine(_root, "iso");
        Write(isoRoot, "dists/questing/Release", "Suite: questing\n");
        Write(isoRoot, "pool/main/test-package_1.0_amd64.deb", "package bytes");

        var error = Assert.Throws<IsoExtractException>(() =>
            IsoExtractService.StageOfflineAptRepositoryFromIsoRoot(ubuntu, isoRoot, Path.Combine(_root, "staging")));

        Assert.Contains("dists/resolute/Release", error.Message);
    }

    [Fact]
    public void UbuntuOfflineAptRepositoryRejectsMissingPackageIndex()
    {
        var ubuntu = DistroRegistry.SupportedDistributions.Single(d => d.Id == "ubuntu");
        var isoRoot = Path.Combine(_root, "iso");
        Write(isoRoot, "dists/resolute/Release", "Suite: resolute\n");
        Write(isoRoot, "pool/main/test-package_1.0_amd64.deb", "package bytes");

        Assert.False(IsoExtractService.HasOfflineAptRepository(ubuntu, isoRoot));
        var error = Assert.Throws<IsoExtractException>(() =>
            IsoExtractService.StageOfflineAptRepositoryFromIsoRoot(ubuntu, isoRoot, Path.Combine(_root, "staging")));

        Assert.Contains("offline APT repository", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

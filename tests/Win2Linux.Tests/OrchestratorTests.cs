using Win2Linux.Core.Boot;
using Win2Linux.Core.Distros;
using Win2Linux.Core.Models;
using Win2Linux.Core.Orchestrator;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Win2Linux.Tests;

public class OrchestratorTests
{
    private readonly ProtectedSystemDisk _protectedDisk = new(
        DiskNumber: 0,
        DiskGuid: "94742ff6-a69f-49c1-80c4-07eb17c0a2cd",
        WindowsDiskSerial: "0025_38F6_51CE_9992.",
        SystemVolume: "C:",
        Status: "PROTECTED_SYSTEM_DISK - CANNOT BE MODIFIED"
    );

    private readonly CandidateTarget _targetSecondaryDisk = new(
        DiskNumber: 1,
        DiskGuid: "c12a7328-f81f-11d2-ba4b-00a0c93ec93b",
        WindowsDiskSerial: "GIGABYTE_GP_GSM2.",
        LinuxDiskSerial: "GIGABYTE_GP-GSM2_serial",
        DriveLetter: "D:",
        VolumeLabel: "Data",
        TotalBytes: 852392673280,
        FreeBytes: 500000000000,
        MaxShrinkBytes: 400000000000,
        IsEligible: true,
        EligibilityReason: "Eligible secondary volume",
        Partitions: new List<PartitionInfo>
        {
            new PartitionInfo(1, 1, null, 16759808, TestMsrGptType, null, 17408),
            new PartitionInfo(1, 2, "D:", 809444384256, TestDataGptType, null, 16777216)
        }
    );

    // Default ESP partition number and size for test generation
    private const int TestEspPartitionNumber = 3;
    private const ulong TestEspSizeBytes = 8589934592; // 8 GB
    private const ulong TestEspOffsetBytes = 809461874688;
    private const string TestEspPartitionGuid = "aabbccdd-eeff-0011-2233-445566778899";
    private const string TestMsrGptType = "e3c9e316-0b5c-4db8-817d-f92df00215ae";
    private const string TestDataGptType = "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7";
    private const string TestEspGptType = "c12a7328-f81f-11d2-ba4b-00a0c93ec93b";

    // Shared valid test config — avoids 'changeme' rejection in GetSha512CryptHash
    private readonly LinuxSetupConfiguration _testConfig = new(
        Password: "TestPassword123!"
    );

    [Fact]
    public async Task StagingRejectsViolationOfSystemDiskInvariant()
    {
        var targetSystemDisk = new CandidateTarget(
            DiskNumber: 0,
            DiskGuid: "94742ff6-a69f-49c1-80c4-07eb17c0a2cd",
            WindowsDiskSerial: "0025_38F6_51CE_9992.",
            LinuxDiskSerial: "0025_38F6_51CE_9992_linux",
            DriveLetter: "C:",
            VolumeLabel: "Windows",
            TotalBytes: 1023348146176,
            FreeBytes: 500000000000,
            MaxShrinkBytes: 400000000000,
            IsEligible: false,
            EligibilityReason: "Cannot use system disk",
            Partitions: new List<PartitionInfo>()
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
            50,
            _testConfig
        );

        var kickstart = InstallationOrchestrator.GenerateUnattendedConfig(plan, TestEspPartitionNumber, TestEspSizeBytes);
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
            50,
            _testConfig
        );

        var kickstart = InstallationOrchestrator.GenerateUnattendedConfig(plan, TestEspPartitionNumber, TestEspSizeBytes);
        Assert.Contains("@^workstation-product-environment", kickstart);
        Assert.DoesNotContain("@^kde-desktop-environment", kickstart);
    }

    [Fact]
    public void LinuxMintAndZorinGenerateValidDualBootConfigs()
    {
        var mint = DistroRegistry.SupportedDistributions.First(d => d.Id == "linuxmint");
        var mintPlan = new InstallationPlan(mint, _targetSecondaryDisk, _protectedDisk, 53687091200, 50, _testConfig);
        var mintConfig = InstallationOrchestrator.GenerateUnattendedConfig(mintPlan, TestEspPartitionNumber, TestEspSizeBytes, TestEspPartitionGuid, TestEspOffsetBytes);
        Assert.Contains("biggest_free", mintConfig);

        var zorin = DistroRegistry.SupportedDistributions.First(d => d.Id == "zorin");
        var zorinPlan = new InstallationPlan(zorin, _targetSecondaryDisk, _protectedDisk, 53687091200, 50, _testConfig);
        var zorinConfig = InstallationOrchestrator.GenerateUnattendedConfig(zorinPlan, TestEspPartitionNumber, TestEspSizeBytes, TestEspPartitionGuid, TestEspOffsetBytes);
        Assert.Contains("partman-auto/init_automatically_partition select biggest_free", zorinConfig);
        Assert.DoesNotContain("d-i partman-auto/method", zorinConfig);
        Assert.DoesNotContain("autoinstall:", zorinConfig);
    }

    [Fact]
    public void FedoraKickstartRespectsCustomSetupConfigurationAndInteractiveReview()
    {
        var fedora = DistroRegistry.SupportedDistributions.First(d => d.Id == "fedora");
        var customSetup = new LinuxSetupConfiguration(
            Locale: "de_DE.UTF-8",
            KeyboardLayout: "de",
            Timezone: "Europe/Berlin",
            Username: "developer",
            RealName: "Jane Developer",
            Password: "SecurePassword123!",
            Hostname: "fedora-workstation",
            InteractiveReview: true
        );

        var plan = new InstallationPlan(
            fedora,
            _targetSecondaryDisk,
            _protectedDisk,
            53687091200,
            50,
            customSetup
        );

        var kickstart = InstallationOrchestrator.GenerateUnattendedConfig(plan, TestEspPartitionNumber, TestEspSizeBytes);

        Assert.Contains("graphical", kickstart);
        Assert.DoesNotContain("reboot", kickstart);
        Assert.Contains("lang de_DE.UTF-8", kickstart);
        Assert.Contains("keyboard de", kickstart);
        Assert.Contains("timezone Europe/Berlin", kickstart);
        Assert.Contains("user --name=developer", kickstart);
        Assert.Contains("hostname=fedora-workstation", kickstart);
    }

    [Fact]
    public void FedoraKickstartEmitsTextAndRebootWhenUnattendedModeSelected()
    {
        var fedora = DistroRegistry.SupportedDistributions.First(d => d.Id == "fedora");
        var unattendedSetup = new LinuxSetupConfiguration(
            InteractiveReview: false,
            Password: "TestPassword123!"
        );

        var plan = new InstallationPlan(
            fedora,
            _targetSecondaryDisk,
            _protectedDisk,
            53687091200,
            50,
            unattendedSetup
        );

        var kickstart = InstallationOrchestrator.GenerateUnattendedConfig(plan, TestEspPartitionNumber, TestEspSizeBytes);

        Assert.Contains("text", kickstart);
        Assert.Contains("reboot", kickstart);
        Assert.DoesNotContain("graphical", kickstart);
    }

    [Fact]
    public void UbuntuAutoinstallGeneratesValidStorageConfig()
    {
        var ubuntu = DistroRegistry.SupportedDistributions.First(d => d.Id == "ubuntu");
        var plan = new InstallationPlan(ubuntu, _targetSecondaryDisk, _protectedDisk, 53687091200, 50, _testConfig);
        var config = InstallationOrchestrator.GenerateUnattendedConfig(plan, TestEspPartitionNumber, TestEspSizeBytes, TestEspPartitionGuid, TestEspOffsetBytes);

        // Must NOT have interactive-sections (removed per user request)
        Assert.DoesNotContain("interactive-sections", config);

        // Must have updates: all
        Assert.Contains("updates: all", config);

        // Resolve the target through the staged ESP partition GUID, not its serial.
        Assert.Contains("path: /dev/win2linux-target", config);
        Assert.DoesNotContain("serial:", config);
    }

    [Fact]
    public void UbuntuAutoinstallCanonicalizesCaseInsensitiveHostname()
    {
        var ubuntu = DistroRegistry.SupportedDistributions.First(d => d.Id == "ubuntu");
        var setup = new LinuxSetupConfiguration(
            Hostname: "ForkMS",
            Password: "TestPassword123!");
        var plan = new InstallationPlan(ubuntu, _targetSecondaryDisk, _protectedDisk, 53687091200, 50, setup);

        var config = InstallationOrchestrator.GenerateUnattendedConfig(plan, TestEspPartitionNumber, TestEspSizeBytes, TestEspPartitionGuid, TestEspOffsetBytes);

        Assert.Contains("hostname: forkms", config);
        InstallationOrchestrator.ValidateAutoinstallYaml(config);
    }

    [Fact]
    public void FedoraKickstartIncludesNvidiaDriversAndRPMFusionWhenRequested()
    {
        var fedora = DistroRegistry.SupportedDistributions.First(d => d.Id == "fedora");
        var nvidiaSetup = new LinuxSetupConfiguration(
            InstallNvidiaDrivers: true,
            InstallProprietaryCodecs: true,
            Password: "TestPassword123!"
        );

        var plan = new InstallationPlan(fedora, _targetSecondaryDisk, _protectedDisk, 53687091200, 50, nvidiaSetup);
        var kickstart = InstallationOrchestrator.GenerateUnattendedConfig(plan, TestEspPartitionNumber, TestEspSizeBytes);

        Assert.Contains("rpmfusion-nonfree", kickstart);
        Assert.Contains("akmod-nvidia", kickstart);
        Assert.Contains("xorg-x11-drv-nvidia-cuda", kickstart);
    }

    [Fact]
    public void FedoraKickstartIncludesLegionSpeakerFixWhenLinux73Selected()
    {
        var fedora = DistroRegistry.SupportedDistributions.First(d => d.Id == "fedora");
        var legionSetup = new LinuxSetupConfiguration(
            KernelSelection: "linux-7.3-legion",
            Password: "TestPassword123!"
        );

        var plan = new InstallationPlan(fedora, _targetSecondaryDisk, _protectedDisk, 53687091200, 50, legionSetup);
        var kickstart = InstallationOrchestrator.GenerateUnattendedConfig(plan, TestEspPartitionNumber, TestEspSizeBytes);

        Assert.Contains("kernel-vanilla-mainline", kickstart);
        Assert.Contains("alsa-legion-speakers.conf", kickstart);
        Assert.Contains("options snd-hda-intel model=dual-codecs", kickstart);
    }

    [Fact]
    public void UbuntuAutoinstallIncludesNvidiaDriversAndLegionAudioFix()
    {
        var ubuntu = DistroRegistry.SupportedDistributions.First(d => d.Id == "ubuntu");
        var setup = new LinuxSetupConfiguration(
            InstallNvidiaDrivers: true,
            KernelSelection: "linux-7.3-legion",
            Password: "TestPassword123!"
        );

        var plan = new InstallationPlan(ubuntu, _targetSecondaryDisk, _protectedDisk, 53687091200, 50, setup);
        var config = InstallationOrchestrator.GenerateUnattendedConfig(plan, TestEspPartitionNumber, TestEspSizeBytes, TestEspPartitionGuid, TestEspOffsetBytes);

        Assert.Contains("ubuntu-drivers", config);
        Assert.Contains("alsa-legion-speakers.conf", config);
        Assert.Contains("options snd-hda-intel model=dual-codecs", config);
    }

    [Fact]
    public async Task InitrdOverlayArchiveBuildsValidCpioWithPrePivotHook()
    {
        var bytes = await InitrdOverlayService.BuildOverlayArchiveBytesAsync();
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
        Assert.Equal(0, bytes.Length % 512);

        var ascii = System.Text.Encoding.ASCII.GetString(bytes);
        Assert.StartsWith("070701", ascii);
        Assert.Contains("usr/lib/dracut/hooks/pre-pivot/99-win2linux.sh", ascii);
        Assert.Contains("TRAILER!!!", ascii);
        Assert.Contains("anaconda --liveinst --kickstart", ascii);
    }

    [Fact]
    public void FedoraGrubConfigLoadsInitrdOverlayInAutomatedMode()
    {
        var fedora = DistroRegistry.SupportedDistributions.First(d => d.Id == "fedora");
        var grubCfg = EspPopulationService.GenerateInstallerGrubConfig(fedora);

        Assert.Contains("initrd ($root)$win2linux_bootdir/initrd", grubCfg);
        Assert.Contains("set win2linux_bootdir=/win2linux/boot", grubCfg);
        Assert.Contains("if search --no-floppy --file --set=win2linux_root /win2linux/boot/vmlinuz; then", grubCfg);
        Assert.Contains("Try or Install Fedora 44", grubCfg);
    }

    [Fact]
    public void FedoraKickstartConfiguresBootloaderWithoutDisablingIt()
    {
        var fedora = DistroRegistry.SupportedDistributions.First(d => d.Id == "fedora");
        var plan = new InstallationPlan(fedora, _targetSecondaryDisk, _protectedDisk, 53687091200, 50, _testConfig);
        var kickstart = InstallationOrchestrator.GenerateUnattendedConfig(plan, TestEspPartitionNumber, TestEspSizeBytes);

        Assert.Contains("bootloader", kickstart);
        Assert.DoesNotContain("bootloader --location=none", kickstart);
    }

    [Fact]
    public void UbuntuAutoinstallTargetsUnallocatedVolumeWithCorrectStorageLayout()
    {
        var ubuntu = DistroRegistry.SupportedDistributions.First(d => d.Id == "ubuntu");
        var plan = new InstallationPlan(ubuntu, _targetSecondaryDisk, _protectedDisk, 53687091200, 50, _testConfig);
        var autoinstall = InstallationOrchestrator.GenerateUnattendedConfig(
            plan,
            TestEspPartitionNumber,
            TestEspSizeBytes,
            TestEspPartitionGuid,
            TestEspOffsetBytes);

        // Must have storage config with preserved disk, ESP, and root partition
        Assert.Contains("id: target-disk", autoinstall);
        Assert.Contains("id: esp-partition", autoinstall);
        Assert.Contains("id: root-partition", autoinstall);
        Assert.Contains("size: -1", autoinstall);
        Assert.Contains("path: /boot/efi", autoinstall);
        Assert.Contains("path: /", autoinstall);
        Assert.Contains("path: /dev/win2linux-target", autoinstall);
        Assert.Contains("/dev/disk/by-partuuid/aabbccdd-eeff-0011-2233-445566778899", autoinstall);
        Assert.Contains("udevadm settle --timeout=10", autoinstall);
        Assert.DoesNotContain("serial:", autoinstall);
        Assert.Contains("early-commands:", autoinstall);
        Assert.Contains("Win2Linux:", autoinstall);
        Assert.Contains("preserve: true", autoinstall);
        Assert.Contains("id: partition-2", autoinstall);
        Assert.Contains("shutdown: reboot", autoinstall);
        Assert.Contains("id: ubuntu-desktop", autoinstall);
        Assert.DoesNotContain("interactive-sections", autoinstall);
        InstallationOrchestrator.ValidateAutoinstallYaml(autoinstall);
    }

    [Fact]
    public void UbuntuGrubConfigLoadsAutoinstallFileFromMediaRoot()
    {
        var ubuntu = DistroRegistry.SupportedDistributions.First(d => d.Id == "ubuntu");
        var grubCfg = EspPopulationService.GenerateInstallerGrubConfig(ubuntu);

        Assert.Contains("boot=casper", grubCfg);
        Assert.Contains("ignore_uuid", grubCfg);
        Assert.Contains("live-media-path=/casper/", grubCfg);
        Assert.Contains("toram", grubCfg);
        Assert.Contains("subiquity.autoinstallpath=/cdrom/autoinstall.yaml", grubCfg);
        Assert.DoesNotContain("ds=nocloud", grubCfg);
    }

    [Fact]
    public void UbuntuGrubConfigFindsKernelAndInitrdWithoutRelyingOnExistingRoot()
    {
        var ubuntu = DistroRegistry.SupportedDistributions.First(d => d.Id == "ubuntu");
        var grubCfg = EspPopulationService.GenerateInstallerGrubConfig(ubuntu);

        Assert.Contains("insmod search_fs_file", grubCfg);
        Assert.Contains("if search --no-floppy --file --set=win2linux_root /win2linux/boot/vmlinuz; then", grubCfg);
        Assert.Contains("if [ -f ($win2linux_root)/win2linux/boot/initrd ]; then", grubCfg);
        Assert.Contains("unset win2linux_root", grubCfg);
        Assert.DoesNotContain("if [ -z \"$root\" ]", grubCfg);
    }

    [Fact]
    public void UbuntuAutoinstallYamlPassesValidation()
    {
        var ubuntu = DistroRegistry.SupportedDistributions.First(d => d.Id == "ubuntu");
        var plan = new InstallationPlan(ubuntu, _targetSecondaryDisk, _protectedDisk, 53687091200, 50, _testConfig);
        var autoinstall = InstallationOrchestrator.GenerateUnattendedConfig(plan, TestEspPartitionNumber, TestEspSizeBytes, TestEspPartitionGuid, TestEspOffsetBytes);

        // This must not throw — the generated YAML must pass our own validator
        InstallationOrchestrator.ValidateAutoinstallYaml(autoinstall);
    }

    [Fact]
    public void ZorinUsesUbiquityPreseedAndPreservesItsExistingEsp()
    {
        var zorin = DistroRegistry.SupportedDistributions.First(d => d.Id == "zorin");
        var plan = new InstallationPlan(zorin, _targetSecondaryDisk, _protectedDisk, 53687091200, 50, _testConfig);
        var preseed = InstallationOrchestrator.GenerateUnattendedConfig(plan, TestEspPartitionNumber, TestEspSizeBytes, TestEspPartitionGuid, TestEspOffsetBytes);
        Assert.Equal("preseed.cfg", InstallationOrchestrator.GetUnattendedFilename("zorin"));
        Assert.Contains("automatic-ubiquity", zorin.AutoinstallKernelArgs);
        Assert.DoesNotContain("subiquity", zorin.AutoinstallKernelArgs);
        Assert.DoesNotContain("ds=nocloud", zorin.AutoinstallKernelArgs);
        Assert.Contains("$reusemethod{ } method{ efi } .", preseed);
        Assert.Contains("ubiquity ubiquity/success_command string", preseed);
        Assert.DoesNotContain("preseed/late_command", preseed);
        Assert.DoesNotContain("confirm_write_new_label", preseed);
        Assert.DoesNotContain("#cloud-config", preseed);
        Assert.Contains("partman-auto/disk string /dev/win2linux-target-unverified", preseed);
        var scripts = InstallationOrchestrator.GenerateZorinInstallerScripts(plan, TestEspPartitionNumber,
            TestEspSizeBytes, TestEspPartitionGuid, TestEspOffsetBytes);
        Assert.Contains("bash /cdrom/win2linux/unattended/early.sh", preseed);
        Assert.Contains("debconf-set partman-auto/disk", scripts["early.sh"]);
        Assert.Contains("partman-auto/init_automatically_partition select biggest_free", preseed);
    }

    [Theory]
    [InlineData(null, TestEspOffsetBytes)]
    [InlineData(TestEspPartitionGuid, null)]
    public void ZorinRefusesMissingEspIdentityOrGeometry(string? guid, ulong? offset)
    {
        var zorin = DistroRegistry.SupportedDistributions.First(d => d.Id == "zorin");
        var plan = new InstallationPlan(zorin, _targetSecondaryDisk, _protectedDisk, 53687091200, 50, _testConfig);
        Assert.Throws<InvalidOperationException>(() => InstallationOrchestrator.GenerateUnattendedConfig(
            plan, TestEspPartitionNumber, TestEspSizeBytes, guid, offset));
    }

    [Fact]
    public void ZorinKernelSetupInstallsVersionedSignedPackagesAndReportsFailure()
    {
        var zorin = DistroRegistry.SupportedDistributions.First(d => d.Id == "zorin");
        var setup = _testConfig with { KernelSelection = "linux-7.3-legion", InstallNvidiaDrivers = true, InteractiveReview = false };
        var plan = new InstallationPlan(zorin, _targetSecondaryDisk, _protectedDisk, 53687091200, 50, setup);
        var preseed = InstallationOrchestrator.GenerateUnattendedConfig(plan, TestEspPartitionNumber,
            TestEspSizeBytes, TestEspPartitionGuid, TestEspOffsetBytes);
        static string DecodeScript(string value) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
            System.Text.RegularExpressions.Regex.Match(value, @"printf '%s' '([A-Za-z0-9+/=]+)' \| base64 -d").Groups[1].Value));
        var scripts = InstallationOrchestrator.GenerateZorinInstallerScripts(plan, TestEspPartitionNumber,
            TestEspSizeBytes, TestEspPartitionGuid, TestEspOffsetBytes);
        var prepare = scripts["prepare.py"];
        Assert.Contains("/dev/disk/by-partuuid/" + TestEspPartitionGuid, prepare);
        Assert.Contains("809444384256", prepare);
        Assert.Contains("809461874688", prepare);
        Assert.Contains("Preserved partition offset changed", prepare);
        Assert.Contains("Largest free extent is not the staged Linux space", prepare);
        Assert.Contains("/lib/partman/commit.d/00win2linux-preserve", prepare);
        Assert.Contains("Installer would delete a preserved partition", prepare);
        var wrapper = scripts["finish.sh"];
        var finish = DecodeScript(wrapper);
        Assert.Contains("Pin-Priority: -1", finish);
        Assert.Contains("debianutils/stonking", finish);
        Assert.Contains("nvidia-headless-no-dkms", finish);
        Assert.Contains("mount --make-rslave", wrapper);
        Assert.Contains("sbverify --list", finish);
        Assert.Contains("modinfo -k", finish);
        Assert.Contains("--no-remove", finish);
        Assert.DoesNotContain("linux-generic/stonking", finish);
        Assert.DoesNotContain("linux-image-unsigned", finish);
        Assert.DoesNotContain("linux-headers", finish);
        Assert.Contains("ubiquity/reboot boolean false", preseed);
        Assert.Contains("systemd-run --unit=win2linux-reboot", wrapper);
        Assert.DoesNotContain("debconf-set-selections", prepare);
        Assert.Contains("exit 1", wrapper);
        Assert.Contains("kernel-7.3-installed", finish);
        Assert.True(finish.IndexOf("cp /boot/efi/EFI/ubuntu/grub.cfg", StringComparison.Ordinal) <
            finish.IndexOf("rm -rf /boot/efi/win2linux", StringComparison.Ordinal));
    }

    [Fact]
    public void ZorinPreseedRejectsMultilineValues()
    {
        var zorin = DistroRegistry.SupportedDistributions.First(d => d.Id == "zorin");
        var plan = new InstallationPlan(zorin, _targetSecondaryDisk, _protectedDisk, 53687091200, 50,
            _testConfig with { RealName = "User\nd-i partman/confirm boolean true" });
        Assert.Throws<InvalidOperationException>(() => InstallationOrchestrator.GenerateUnattendedConfig(
            plan, TestEspPartitionNumber, TestEspSizeBytes, TestEspPartitionGuid, TestEspOffsetBytes));
    }

    [Fact]
    public void ZorinWifiUsesAProtectedNetworkManagerProfileWithoutAKeyInPreseedOrProcessArguments()
    {
        var zorin = DistroRegistry.SupportedDistributions.First(d => d.Id == "zorin");
        var wifi = new WifiNetworkConfiguration("Test;Network", "TestWifiKey73!", false);
        var plan = new InstallationPlan(zorin, _targetSecondaryDisk, _protectedDisk, 53687091200, 50,
            _testConfig with { WifiNetwork = wifi, KernelSelection = "linux-7.3-legion" });
        var preseed = InstallationOrchestrator.GenerateUnattendedConfig(plan, TestEspPartitionNumber,
            TestEspSizeBytes, TestEspPartitionGuid, TestEspOffsetBytes);
        var scripts = InstallationOrchestrator.GenerateZorinInstallerScripts(plan, TestEspPartitionNumber,
            TestEspSizeBytes, TestEspPartitionGuid, TestEspOffsetBytes);
        Assert.DoesNotContain(wifi.Password!, preseed);
        Assert.Contains("('wifi','ssid',wifi['Ssid'])", scripts["prepare.py"]);
        Assert.Contains("os.fchmod(fd,0o600)", scripts["prepare.py"]);
        Assert.Contains("network_command('nmcli','--wait','45','connection','up','uuid',connection_uuid)", scripts["prepare.py"]);
        Assert.DoesNotContain("'nmcli','dev','wifi','connect'", scripts["prepare.py"]);
        Assert.Contains("install -D -m 0600", scripts["finish.sh"]);
    }

    [Fact]
    public void UbuntuCleanupReplacesEntireFallbackBootChainWithInstalledFiles()
    {
        var ubuntu = DistroRegistry.SupportedDistributions.First(d => d.Id == "ubuntu");
        var plan = new InstallationPlan(ubuntu, _targetSecondaryDisk, _protectedDisk, 53687091200, 50, _testConfig);
        var config = InstallationOrchestrator.GenerateUnattendedConfig(
            plan, TestEspPartitionNumber, TestEspSizeBytes, TestEspPartitionGuid, TestEspOffsetBytes);
        var root = (YamlMappingNode)LoadYaml(config).Documents[0].RootNode;
        var autoinstall = (YamlMappingNode)root.Children[new YamlScalarNode("autoinstall")];
        var lateCommands = ((YamlSequenceNode)autoinstall.Children[new YamlScalarNode("late-commands")])
            .Children.Cast<YamlScalarNode>().Select(command => command.Value!).ToList();

        // Replacing shim and its config while retaining the installer GRUB
        // binary leaves a mixed fallback chain after first-boot cleanup.
        foreach (var (source, target) in new[]
        {
            ("grub.cfg", "grub.cfg"),
            ("grubx64.efi", "grubx64.efi"),
            ("shimx64.efi", "bootx64.efi")
        })
        {
            var copy = $"ExecStart=/bin/cp /boot/efi/EFI/ubuntu/{source} /boot/efi/EFI/boot/{target}";
            var command = Assert.Single(lateCommands.Where(command => command.Contains(copy, StringComparison.Ordinal)));
            Assert.Contains("/etc/systemd/system/win2linux-cleanup.service", command);
        }
        Assert.Contains(lateCommands, command => command.EndsWith("systemctl enable win2linux-cleanup.service", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ubuntu")]
    public void AutoinstallPreservesExactExistingGptLayout(string distroId)
    {
        var distro = DistroRegistry.SupportedDistributions.First(d => d.Id == distroId);
        var plan = new InstallationPlan(distro, _targetSecondaryDisk, _protectedDisk, 53687091200, 50, _testConfig);
        var config = InstallationOrchestrator.GenerateUnattendedConfig(
            plan, TestEspPartitionNumber, TestEspSizeBytes, TestEspPartitionGuid, TestEspOffsetBytes);
        var yaml = LoadYaml(config);

        Assert.Equal("2", Scalar(StorageMapping(yaml), "version"));
        var partitions = PartitionActions(yaml);
        Assert.Equal(new[] { "partition-1", "partition-2", "esp-partition", "root-partition" },
            partitions.Select(part => Scalar(part, "id")));

        // Windows starts this MSR at sector 34; rounding it to 1 MiB produces
        // the preserved-partition offset lookup failure from the installer log.
        AssertPreservedPartition(partitions[0], "1", "17408", "16759808", TestMsrGptType);
        AssertPreservedPartition(partitions[1], "2", "16777216", "809444384256", TestDataGptType);
        AssertPreservedPartition(partitions[2], "3", "809461874688", "8589934592", TestEspGptType);
        Assert.Equal("boot", Scalar(partitions[2], "flag"));

        var newPartition = Assert.Single(partitions.Where(part => Scalar(part, "preserve") != "true"));
        Assert.Equal("root-partition", Scalar(newPartition, "id"));
        Assert.Equal("false", Scalar(newPartition, "preserve"));
        Assert.Equal("-1", Scalar(newPartition, "size"));
        InstallationOrchestrator.ValidateAutoinstallYaml(config);
    }

    [Fact]
    public void AutoinstallPlacesRootAfterLastPhysicalPartitionWhenEspIsEarlier()
    {
        const ulong gib = 1024UL * 1024 * 1024;
        var target = _targetSecondaryDisk with
        {
            Partitions = new List<PartitionInfo>
            {
                new(1, 8, "D:", 4 * gib, TestDataGptType, null, 32 * gib),
                new(1, 2, null, 16759808, TestMsrGptType, null, 17408)
            }
        };
        var ubuntu = DistroRegistry.SupportedDistributions.First(d => d.Id == "ubuntu");
        var plan = new InstallationPlan(ubuntu, target, _protectedDisk, 53687091200, 50, _testConfig);
        var config = InstallationOrchestrator.GenerateUnattendedConfig(
            plan, TestEspPartitionNumber, TestEspSizeBytes, TestEspPartitionGuid, gib);
        var partitions = PartitionActions(LoadYaml(config));

        // GPT slot numbers and provider enumeration do not describe physical
        // order. Retain the gaps and place the new root after the trailing data.
        Assert.Equal(new[] { "partition-2", "esp-partition", "partition-8", "root-partition" },
            partitions.Select(part => Scalar(part, "id")));
        AssertPreservedPartition(partitions[0], "2", "17408", "16759808", TestMsrGptType);
        AssertPreservedPartition(partitions[1], "3", "1073741824", "8589934592", TestEspGptType);
        AssertPreservedPartition(partitions[2], "8", "34359738368", "4294967296", TestDataGptType);
        Assert.Equal("false", Scalar(partitions[3], "preserve"));
        Assert.Equal("-1", Scalar(partitions[3], "size"));
        InstallationOrchestrator.ValidateAutoinstallYaml(config);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, 0UL)]
    [InlineData(true, null)]
    [InlineData(true, 0UL)]
    public void AutoinstallRejectsUnknownOrZeroExistingPartitionOffsets(bool isEsp, ulong? offsetBytes)
    {
        var target = _targetSecondaryDisk with
        {
            Partitions = _targetSecondaryDisk.Partitions
                .Select(part => !isEsp && part.PartitionNumber == 1 ? part with { OffsetBytes = offsetBytes } : part)
                .ToList()
        };
        var ubuntu = DistroRegistry.SupportedDistributions.First(d => d.Id == "ubuntu");
        var plan = new InstallationPlan(ubuntu, target, _protectedDisk, 53687091200, 50, _testConfig);

        var error = Assert.Throws<InvalidOperationException>(() => InstallationOrchestrator.GenerateUnattendedConfig(
            plan, TestEspPartitionNumber, TestEspSizeBytes, TestEspPartitionGuid,
            isEsp ? offsetBytes : TestEspOffsetBytes));
        Assert.Contains("offset", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Basic Data")]
    public void AutoinstallRejectsUnverifiedPreservedGptTypes(string? gptType)
    {
        var target = _targetSecondaryDisk with
        {
            Partitions = _targetSecondaryDisk.Partitions
                .Select(part => part.PartitionNumber == 2 ? part with { GptType = gptType } : part)
                .ToList()
        };
        var ubuntu = DistroRegistry.SupportedDistributions.First(d => d.Id == "ubuntu");
        var plan = new InstallationPlan(ubuntu, target, _protectedDisk, 53687091200, 50, _testConfig);

        var error = Assert.Throws<InvalidOperationException>(() => InstallationOrchestrator.GenerateUnattendedConfig(
            plan, TestEspPartitionNumber, TestEspSizeBytes, TestEspPartitionGuid, TestEspOffsetBytes));
        Assert.Contains("GPT type", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AutoinstallRejectsDuplicateSlotsOrOverlappingPartitions(bool duplicateSlots)
    {
        var target = _targetSecondaryDisk with
        {
            Partitions = _targetSecondaryDisk.Partitions
                .Select(part => part.PartitionNumber != 2 ? part : duplicateSlots
                    ? part with { PartitionNumber = 1 }
                    : part with { OffsetBytes = 1048576 })
                .ToList()
        };
        var ubuntu = DistroRegistry.SupportedDistributions.First(d => d.Id == "ubuntu");
        var plan = new InstallationPlan(ubuntu, target, _protectedDisk, 53687091200, 50, _testConfig);

        var error = Assert.Throws<InvalidOperationException>(() => InstallationOrchestrator.GenerateUnattendedConfig(
            plan, TestEspPartitionNumber, TestEspSizeBytes, TestEspPartitionGuid, TestEspOffsetBytes));
        Assert.Contains(duplicateSlots ? "duplicate" : "overlapping", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("partition-1", false)]
    [InlineData("partition-1", true)]
    [InlineData("esp-partition", false)]
    [InlineData("esp-partition", true)]
    public void AutoinstallValidatorRejectsMissingOrZeroPreservedOffsets(string partitionId, bool zeroOffset)
    {
        var ubuntu = DistroRegistry.SupportedDistributions.First(d => d.Id == "ubuntu");
        var plan = new InstallationPlan(ubuntu, _targetSecondaryDisk, _protectedDisk, 53687091200, 50, _testConfig);
        var config = InstallationOrchestrator.GenerateUnattendedConfig(
            plan, TestEspPartitionNumber, TestEspSizeBytes, TestEspPartitionGuid, TestEspOffsetBytes);
        var yaml = LoadYaml(config);
        var partition = PartitionActions(yaml).Single(part => Scalar(part, "id") == partitionId);
        var offsetKey = new YamlScalarNode("offset");
        if (zeroOffset)
            partition.Children[offsetKey] = new YamlScalarNode("0");
        else
            partition.Children.Remove(offsetKey);
        using var writer = new StringWriter();
        yaml.Save(writer, false);

        var error = Assert.Throws<InvalidOperationException>(() =>
            InstallationOrchestrator.ValidateAutoinstallYaml(writer.ToString()));
        Assert.Contains("offset", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static YamlStream LoadYaml(string content)
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(content);
        yaml.Load(reader);
        return yaml;
    }

    private static YamlMappingNode StorageMapping(YamlStream yaml)
    {
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var autoinstall = (YamlMappingNode)root.Children[new YamlScalarNode("autoinstall")];
        return (YamlMappingNode)autoinstall.Children[new YamlScalarNode("storage")];
    }

    private static List<YamlMappingNode> PartitionActions(YamlStream yaml)
    {
        var config = (YamlSequenceNode)StorageMapping(yaml).Children[new YamlScalarNode("config")];
        return config.Children.Cast<YamlMappingNode>()
            .Where(action => Scalar(action, "type") == "partition")
            .ToList();
    }

    private static string? Scalar(YamlMappingNode map, string key)
    {
        return map.Children.TryGetValue(new YamlScalarNode(key), out var node)
            ? ((YamlScalarNode)node).Value
            : null;
    }

    private static void AssertPreservedPartition(YamlMappingNode partition, string number, string offset, string size, string partitionType)
    {
        Assert.Equal(number, Scalar(partition, "number"));
        Assert.Equal(offset, Scalar(partition, "offset"));
        Assert.Equal(size, Scalar(partition, "size"));
        Assert.Equal(partitionType, Scalar(partition, "partition_type"));
        Assert.Equal("true", Scalar(partition, "preserve"));
    }
}

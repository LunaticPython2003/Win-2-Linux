namespace Win2Linux.Core.Distros;

public record DesktopEnvironmentOption(
    string Id,
    string DisplayName,
    string Description,
    string PackageGroup,
    string IsoDownloadUrl,
    string ChecksumUrl,
    string[]? IsoMirrorUrls = null
);

public record DistroProfile(
    string Id,
    string DisplayName,
    string Description,
    string AccentColor,
    string SecureBootStatus,
    string SecureBootSigner,
    string EfiVendorDir,
    string EfiBinary,
    string UefiBootLabel,
    string DefaultFilesystem,
    string DefaultVersion,
    string IsoDownloadUrl,
    string ChecksumUrl,
    string UnattendedMechanism,
    // ISO content paths — distro-specific locations inside the ISO image
    string IsoKernelPath,
    string IsoInitrdPath,
    string IsoShimPath,
    string IsoGrubEfiPath,
    string AutoinstallKernelArgs,
    string? InteractiveKernelArgs = null,
    // Ordered mirror fallbacks tried before IsoDownloadUrl (fastest/closest first).
    // If empty, IsoDownloadUrl is used directly.
    string[]? IsoMirrorUrls = null,
    // Desktop Environment options (e.g. for Fedora: KDE Plasma vs GNOME)
    IReadOnlyList<DesktopEnvironmentOption>? DesktopEnvironments = null,
    string? SelectedDesktopEnvironment = null
)
{
    public string GetInteractiveKernelArgs() => InteractiveKernelArgs ?? AutoinstallKernelArgs;

    public DistroProfile WithDesktopEnvironment(string deId)
    {
        if (DesktopEnvironments == null || DesktopEnvironments.Count == 0)
            return this;

        var option = DesktopEnvironments.FirstOrDefault(de => de.Id.Equals(deId, StringComparison.OrdinalIgnoreCase));
        if (option == null)
            return this;

        return this with
        {
            SelectedDesktopEnvironment = option.Id,
            IsoDownloadUrl = option.IsoDownloadUrl,
            ChecksumUrl = option.ChecksumUrl,
            IsoMirrorUrls = option.IsoMirrorUrls ?? IsoMirrorUrls
        };
    }
}

public static class DistroRegistry
{
    public static IReadOnlyList<DistroProfile> SupportedDistributions { get; } = new List<DistroProfile>
    {
        new(
            Id: "ubuntu",
            DisplayName: "Ubuntu 26.04 LTS (Resolute Raccoon)",
            Description: "Industry-standard, highly compatible Long Term Support release with official Microsoft UEFI CA signed shim.",
            AccentColor: "#E95420",
            SecureBootStatus: "Certified (Official Microsoft UEFI CA)",
            SecureBootSigner: "Canonical Ltd.",
            EfiVendorDir: "ubuntu",
            EfiBinary: "shimx64.efi",
            UefiBootLabel: "ubuntu",
            DefaultFilesystem: "ext4",
            DefaultVersion: "26.04.1",
            IsoDownloadUrl: "https://releases.ubuntu.com/26.04.1/ubuntu-26.04.1-live-server-amd64.iso",
            ChecksumUrl: "https://releases.ubuntu.com/26.04.1/SHA256SUMS",
            UnattendedMechanism: "Subiquity Autoinstall (cloud-init)",
            IsoKernelPath: "casper/vmlinuz",
            IsoInitrdPath: "casper/initrd",
            IsoShimPath: "EFI/BOOT/BOOTX64.EFI",
            IsoGrubEfiPath: "EFI/ubuntu/grubx64.efi",
            AutoinstallKernelArgs: "autoinstall toram quiet splash --- ds=nocloud;s=/cdrom/win2linux/unattended/",
            InteractiveKernelArgs: "toram quiet splash ---"
        ),
        new(
            Id: "fedora",
            DisplayName: "Fedora 44",
            Description: "Modern, cutting-edge desktop Linux distribution. Features full live ISO with complete offline installer and official Microsoft UEFI CA signed shim.",
            AccentColor: "#1D99F3",
            SecureBootStatus: "Certified (Official Microsoft UEFI CA)",
            SecureBootSigner: "Red Hat Inc.",
            EfiVendorDir: "fedora",
            EfiBinary: "shimx64.efi",
            UefiBootLabel: "Fedora",
            DefaultFilesystem: "btrfs",
            DefaultVersion: "44-1.7",
            IsoDownloadUrl: "https://dl.fedoraproject.org/pub/fedora/linux/releases/44/KDE/x86_64/iso/Fedora-KDE-Desktop-Live-44-1.7.x86_64.iso",
            ChecksumUrl: "https://dl.fedoraproject.org/pub/fedora/linux/releases/44/KDE/x86_64/iso/Fedora-KDE-44-1.7-x86_64-CHECKSUM",
            UnattendedMechanism: "Anaconda Kickstart (ks.cfg)",
            IsoKernelPath: "images/pxeboot/vmlinuz",
            IsoInitrdPath: "images/pxeboot/initrd.img",
            IsoShimPath: "EFI/BOOT/BOOTX64.EFI",
            IsoGrubEfiPath: "EFI/fedora/grubx64.efi",
            AutoinstallKernelArgs: "root=live:CDLABEL=LINUXEFI rd.live.image rd.live.ram=1 inst.ks=hd:LABEL=LINUXEFI:/win2linux/unattended/kickstart.ks quiet rhgb",
            InteractiveKernelArgs: "root=live:CDLABEL=LINUXEFI rd.live.image rd.live.ram=1 quiet rhgb",
            IsoMirrorUrls: [
                "https://mirrors.tuna.tsinghua.edu.cn/fedora/releases/44/KDE/x86_64/iso/Fedora-KDE-Desktop-Live-44-1.7.x86_64.iso",
                "https://mirrors.ustc.edu.cn/fedora/releases/44/KDE/x86_64/iso/Fedora-KDE-Desktop-Live-44-1.7.x86_64.iso",
                "https://dl.fedoraproject.org/pub/fedora/linux/releases/44/KDE/x86_64/iso/Fedora-KDE-Desktop-Live-44-1.7.x86_64.iso",
            ],
            DesktopEnvironments: [
                new(
                    Id: "kde",
                    DisplayName: "KDE Plasma",
                    Description: "Lightweight, beautifully customizable desktop with Qt 6 and Wayland (Preferred).",
                    PackageGroup: "@^kde-desktop-environment",
                    IsoDownloadUrl: "https://dl.fedoraproject.org/pub/fedora/linux/releases/44/KDE/x86_64/iso/Fedora-KDE-Desktop-Live-44-1.7.x86_64.iso",
                    ChecksumUrl: "https://dl.fedoraproject.org/pub/fedora/linux/releases/44/KDE/x86_64/iso/Fedora-KDE-44-1.7-x86_64-CHECKSUM",
                    IsoMirrorUrls: [
                        "https://mirrors.tuna.tsinghua.edu.cn/fedora/releases/44/KDE/x86_64/iso/Fedora-KDE-Desktop-Live-44-1.7.x86_64.iso",
                        "https://mirrors.ustc.edu.cn/fedora/releases/44/KDE/x86_64/iso/Fedora-KDE-Desktop-Live-44-1.7.x86_64.iso",
                        "https://dl.fedoraproject.org/pub/fedora/linux/releases/44/KDE/x86_64/iso/Fedora-KDE-Desktop-Live-44-1.7.x86_64.iso",
                    ]
                ),
                new(
                    Id: "gnome",
                    DisplayName: "GNOME Workstation",
                    Description: "Standard default Fedora Workstation desktop environment with GNOME Shell.",
                    PackageGroup: "@^workstation-product-environment",
                    IsoDownloadUrl: "https://dl.fedoraproject.org/pub/fedora/linux/releases/44/Workstation/x86_64/iso/Fedora-Workstation-Live-44-1.7.x86_64.iso",
                    ChecksumUrl: "https://dl.fedoraproject.org/pub/fedora/linux/releases/44/Workstation/x86_64/iso/Fedora-Workstation-44-1.7-x86_64-CHECKSUM",
                    IsoMirrorUrls: [
                        "https://mirrors.tuna.tsinghua.edu.cn/fedora/releases/44/Workstation/x86_64/iso/Fedora-Workstation-Live-44-1.7.x86_64.iso",
                        "https://mirrors.ustc.edu.cn/fedora/releases/44/Workstation/x86_64/iso/Fedora-Workstation-Live-44-1.7.x86_64.iso",
                        "https://dl.fedoraproject.org/pub/fedora/linux/releases/44/Workstation/x86_64/iso/Fedora-Workstation-Live-44-1.7.x86_64.iso",
                    ]
                )
            ],
            SelectedDesktopEnvironment: "kde"
        ),
        new(
            Id: "linuxmint",
            DisplayName: "Linux Mint 22.1 (Xia)",
            Description: "Elegant, modern, and familiar desktop experience based on Ubuntu LTS. Features Microsoft UEFI CA signed shim and out-of-the-box multimedia support.",
            AccentColor: "#87CF3E",
            SecureBootStatus: "Certified (Official Microsoft UEFI CA)",
            SecureBootSigner: "Canonical Ltd.",
            EfiVendorDir: "ubuntu",
            EfiBinary: "shimx64.efi",
            UefiBootLabel: "Linux Mint",
            DefaultFilesystem: "ext4",
            DefaultVersion: "22.1",
            IsoDownloadUrl: "https://mirrors.kernel.org/linuxmint/stable/22.1/linuxmint-22.1-cinnamon-64bit.iso",
            ChecksumUrl: "https://ftp.heanet.ie/mirrors/linuxmint.com/stable/22.1/sha256sum.txt",
            UnattendedMechanism: "Ubiquity Preseed (preseed.cfg)",
            IsoKernelPath: "casper/vmlinuz",
            IsoInitrdPath: "casper/initrd.lz",
            IsoShimPath: "EFI/BOOT/BOOTX64.EFI",
            IsoGrubEfiPath: "EFI/BOOT/grubx64.efi",
            AutoinstallKernelArgs: "boot=casper toram automatic-ubiquity quiet splash ---",
            InteractiveKernelArgs: "boot=casper toram quiet splash ---",
            IsoMirrorUrls: [
                "https://mirrors.kernel.org/linuxmint/stable/22.1/linuxmint-22.1-cinnamon-64bit.iso",
                "https://mirrors.layeronline.com/linuxmint/stable/22.1/linuxmint-22.1-cinnamon-64bit.iso"
            ]
        ),
        new(
            Id: "zorin",
            DisplayName: "Zorin OS 17.2",
            Description: "Stunning Windows-like desktop designed for seamless transition from Windows. Signed by Canonical under Microsoft UEFI CA for effortless dual-booting.",
            AccentColor: "#13A5E5",
            SecureBootStatus: "Certified (Official Microsoft UEFI CA)",
            SecureBootSigner: "Canonical Ltd.",
            EfiVendorDir: "ubuntu",
            EfiBinary: "shimx64.efi",
            UefiBootLabel: "Zorin",
            DefaultFilesystem: "ext4",
            DefaultVersion: "17.2",
            IsoDownloadUrl: "https://mirrors.edge.kernel.org/zorinos-isos/17/Zorin-OS-17.2-Core-64-bit.iso",
            ChecksumUrl: "https://mirrors.edge.kernel.org/zorinos-isos/17/Zorin-OS-17.2-Core-64-bit.iso.sha256",
            UnattendedMechanism: "Subiquity Autoinstall (cloud-init)",
            IsoKernelPath: "casper/vmlinuz",
            IsoInitrdPath: "casper/initrd.lz",
            IsoShimPath: "EFI/BOOT/BOOTX64.EFI",
            IsoGrubEfiPath: "EFI/BOOT/grubx64.efi",
            AutoinstallKernelArgs: "boot=casper toram quiet splash --- ds=nocloud;s=/cdrom/win2linux/unattended/",
            InteractiveKernelArgs: "boot=casper toram quiet splash ---",
            IsoMirrorUrls: [
                "https://mirrors.edge.kernel.org/zorinos-isos/17/Zorin-OS-17.2-Core-64-bit.iso"
            ]
        ),
    };
}

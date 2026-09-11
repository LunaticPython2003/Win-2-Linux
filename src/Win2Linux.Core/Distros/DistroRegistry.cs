namespace Win2Linux.Core.Distros;

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
    // Ordered mirror fallbacks tried before IsoDownloadUrl (fastest/closest first).
    // If empty, IsoDownloadUrl is used directly.
    string[]? IsoMirrorUrls = null
);

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
            AutoinstallKernelArgs: "autoinstall quiet splash --- ds=nocloud;s=/cdrom/win2linux/unattended/"
        ),
        new(
            Id: "fedora",
            DisplayName: "Fedora Workstation 44",
            Description: "Modern, cutting-edge desktop Linux distribution with GNOME. Full live ISO (2.7 GB) — includes a complete offline installer. Red Hat is the primary author and maintainer of the UEFI shim.",
            AccentColor: "#294172",
            SecureBootStatus: "Certified (Official Microsoft UEFI CA)",
            SecureBootSigner: "Red Hat Inc.",
            EfiVendorDir: "fedora",
            EfiBinary: "shimx64.efi",
            UefiBootLabel: "Fedora",
            DefaultFilesystem: "btrfs",
            DefaultVersion: "44-1.7",
            IsoDownloadUrl: "https://dl.fedoraproject.org/pub/fedora/linux/releases/44/Workstation/x86_64/iso/Fedora-Workstation-Live-44-1.7.x86_64.iso",
            ChecksumUrl: "https://dl.fedoraproject.org/pub/fedora/linux/releases/44/Workstation/x86_64/iso/Fedora-Workstation-44-1.7-x86_64-CHECKSUM",
            UnattendedMechanism: "Anaconda Kickstart (ks.cfg)",
            IsoKernelPath: "images/pxeboot/vmlinuz",
            IsoInitrdPath: "images/pxeboot/initrd.img",
            IsoShimPath: "EFI/BOOT/BOOTX64.EFI",
            IsoGrubEfiPath: "EFI/fedora/grubx64.efi",
            AutoinstallKernelArgs: "root=live:CDLABEL=LINUXEFI rd.live.image inst.ks=hd:LABEL=LINUXEFI:/win2linux/unattended/kickstart.ks quiet rhgb",
            IsoMirrorUrls: [
                // Tsinghua TUNA (Beijing CDN — excellent peering with India via Singapore)
                "https://mirrors.tuna.tsinghua.edu.cn/fedora/releases/44/Workstation/x86_64/iso/Fedora-Workstation-Live-44-1.7.x86_64.iso",
                // USTC (University of Science and Technology of China — reliable alternate)
                "https://mirrors.ustc.edu.cn/fedora/releases/44/Workstation/x86_64/iso/Fedora-Workstation-Live-44-1.7.x86_64.iso",
                // Official Fedora CDN (last resort — may be geo-throttled from India)
                "https://dl.fedoraproject.org/pub/fedora/linux/releases/44/Workstation/x86_64/iso/Fedora-Workstation-Live-44-1.7.x86_64.iso",
            ]
        ),
        new(
            Id: "fedora-netinstall",
            DisplayName: "Fedora Workstation 44 (Netinstall)",
            Description: "Fedora 44 network installer — minimal 1.1 GB ISO that downloads packages at install time. Requires a working internet connection during installation.",
            AccentColor: "#3C5A99",
            SecureBootStatus: "Certified (Official Microsoft UEFI CA)",
            SecureBootSigner: "Red Hat Inc.",
            EfiVendorDir: "fedora",
            EfiBinary: "shimx64.efi",
            UefiBootLabel: "Fedora",
            DefaultFilesystem: "btrfs",
            DefaultVersion: "44-1.7-netinstall",
            IsoDownloadUrl: "https://dl.fedoraproject.org/pub/fedora/linux/releases/44/Everything/x86_64/iso/Fedora-Everything-netinst-x86_64-44-1.7.iso",
            ChecksumUrl: "https://dl.fedoraproject.org/pub/fedora/linux/releases/44/Everything/x86_64/iso/Fedora-Everything-44-1.7-x86_64-CHECKSUM",
            UnattendedMechanism: "Anaconda Kickstart (ks.cfg)",
            IsoKernelPath: "images/pxeboot/vmlinuz",
            IsoInitrdPath: "images/pxeboot/initrd.img",
            IsoShimPath: "EFI/BOOT/BOOTX64.EFI",
            IsoGrubEfiPath: "EFI/fedora/grubx64.efi",
            AutoinstallKernelArgs: "inst.repo=https://dl.fedoraproject.org/pub/fedora/linux/releases/44/Everything/x86_64/os/ inst.ks=hd:LABEL=LINUXEFI:/win2linux/unattended/kickstart.ks quiet rhgb",
            IsoMirrorUrls: [
                // Tsinghua TUNA (Beijing CDN — excellent peering with India via Singapore)
                "https://mirrors.tuna.tsinghua.edu.cn/fedora/releases/44/Everything/x86_64/iso/Fedora-Everything-netinst-x86_64-44-1.7.iso",
                // USTC (University of Science and Technology of China — reliable alternate)
                "https://mirrors.ustc.edu.cn/fedora/releases/44/Everything/x86_64/iso/Fedora-Everything-netinst-x86_64-44-1.7.iso",
                // Official Fedora CDN (last resort — may be geo-throttled from India)
                "https://dl.fedoraproject.org/pub/fedora/linux/releases/44/Everything/x86_64/iso/Fedora-Everything-netinst-x86_64-44-1.7.iso",
            ]
        ),
        new(
            Id: "debian",
            DisplayName: "Debian 12 (Bookworm)",
            Description: "The universal operating system, renowned for rock-solid stability and predictable package management.",
            AccentColor: "#A80030",
            SecureBootStatus: "Certified (Official Microsoft UEFI CA)",
            SecureBootSigner: "Debian Project",
            EfiVendorDir: "debian",
            EfiBinary: "shimx64.efi",
            UefiBootLabel: "debian",
            DefaultFilesystem: "ext4",
            DefaultVersion: "12.8.0",
            IsoDownloadUrl: "https://cdimage.debian.org/debian-cd/current/amd64/iso-cd/debian-12.8.0-amd64-netinst.iso",
            ChecksumUrl: "https://cdimage.debian.org/debian-cd/current/amd64/iso-cd/SHA256SUMS",
            UnattendedMechanism: "Debian Preseed (preseed.cfg)",
            IsoKernelPath: "install.amd/vmlinuz",
            IsoInitrdPath: "install.amd/initrd.gz",
            IsoShimPath: "EFI/BOOT/BOOTX64.EFI",
            IsoGrubEfiPath: "EFI/debian/grubx64.efi",
            AutoinstallKernelArgs: "auto=true priority=critical preseed/file=/cdrom/win2linux/unattended/preseed.cfg quiet"
        ),
        new(
            Id: "opensuse",
            DisplayName: "openSUSE Tumbleweed",
            Description: "Rolling release powerhouse with native Snapper rollback support and first-class Secure Boot signing.",
            AccentColor: "#173f35",
            SecureBootStatus: "Certified (Official Microsoft UEFI CA)",
            SecureBootSigner: "SUSE LLC",
            EfiVendorDir: "opensuse",
            EfiBinary: "shim.efi",
            UefiBootLabel: "openSUSE",
            DefaultFilesystem: "btrfs",
            DefaultVersion: "Snapshot",
            IsoDownloadUrl: "https://download.opensuse.org/tumbleweed/iso/openSUSE-Tumbleweed-NET-x86_64-Current.iso",
            ChecksumUrl: "https://download.opensuse.org/tumbleweed/iso/openSUSE-Tumbleweed-NET-x86_64-Current.iso.sha256",
            UnattendedMechanism: "AutoYaST (autoinst.xml)",
            IsoKernelPath: "boot/x86_64/loader/linux",
            IsoInitrdPath: "boot/x86_64/loader/initrd",
            IsoShimPath: "EFI/BOOT/BOOTX64.EFI",
            IsoGrubEfiPath: "EFI/opensuse/grubx64.efi",
            AutoinstallKernelArgs: "root=live:CDLABEL=LINUXEFI rd.live.image rd.live.overlay.overlayfs=1 autoyast=device://disk/by-label/LINUXEFI/win2linux/unattended/autoyast.xml quiet splash"
        ),
    };
}

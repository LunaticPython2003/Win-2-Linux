use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DistroInfo {
    pub id: String,
    pub display_name: String,
    pub description: String,
    pub accent_color: String,
    pub secure_boot_status: String,
    pub secure_boot_signer: String,
    pub efi_vendor_dir: String,
    pub efi_binary: String,
    pub uefi_boot_label: String,
    pub default_filesystem: String,
    pub default_version: String,
    pub iso_download_url: String,
    pub checksum_url: String,
    pub unattended_mechanism: String, // "Subiquity Autoinstall", "Anaconda Kickstart", "Preseed"
}

pub fn get_supported_distributions() -> Vec<DistroInfo> {
    vec![
        DistroInfo {
            id: "ubuntu".to_string(),
            display_name: "Ubuntu 24.04 LTS (Noble Numbat)".to_string(),
            description: "Industry-standard, highly compatible LTS release with official Microsoft UEFI CA signed shim.".to_string(),
            accent_color: "#E95420".to_string(),
            secure_boot_status: "Certified - First Class".to_string(),
            secure_boot_signer: "Canonical Ltd. (Microsoft Corporation UEFI CA 2011/2023)".to_string(),
            efi_vendor_dir: "ubuntu".to_string(),
            efi_binary: "shimx64.efi".to_string(),
            uefi_boot_label: "ubuntu".to_string(),
            default_filesystem: "ext4".to_string(),
            default_version: "24.04.1".to_string(),
            iso_download_url: "https://releases.ubuntu.com/24.04.1/ubuntu-24.04.1-live-server-amd64.iso".to_string(),
            checksum_url: "https://releases.ubuntu.com/24.04.1/SHA256SUMS".to_string(),
            unattended_mechanism: "Subiquity Autoinstall (cloud-init)".to_string(),
        },
        DistroInfo {
            id: "fedora".to_string(),
            display_name: "Fedora Workstation 41".to_string(),
            description: "Modern, cutting-edge desktop Linux distribution. Red Hat is the upstream author and maintainer of UEFI shim.".to_string(),
            accent_color: "#294172".to_string(),
            secure_boot_status: "Certified - Upstream Author".to_string(),
            secure_boot_signer: "Red Hat Inc. (Microsoft Corporation UEFI CA 2011/2023)".to_string(),
            efi_vendor_dir: "fedora".to_string(),
            efi_binary: "shimx64.efi".to_string(),
            uefi_boot_label: "Fedora".to_string(),
            default_filesystem: "btrfs".to_string(),
            default_version: "41-1.4".to_string(),
            iso_download_url: "https://download.fedoraproject.org/pub/fedora/linux/releases/41/Workstation/x86_64/iso/Fedora-Workstation-Live-x86_64-41-1.4.iso".to_string(),
            checksum_url: "https://download.fedoraproject.org/pub/fedora/linux/releases/41/Workstation/x86_64/iso/Fedora-Workstation-41-1.4-x86_64-CHECKSUM".to_string(),
            unattended_mechanism: "Anaconda Kickstart (ks.cfg)".to_string(),
        },
        DistroInfo {
            id: "debian".to_string(),
            display_name: "Debian 12 (Bookworm)".to_string(),
            description: "The universal operating system, famous for rock-solid stability and clean package management.".to_string(),
            accent_color: "#A80030".to_string(),
            secure_boot_status: "Certified - First Class".to_string(),
            secure_boot_signer: "Debian Project (Microsoft Corporation UEFI CA 2011/2023)".to_string(),
            efi_vendor_dir: "debian".to_string(),
            efi_binary: "shimx64.efi".to_string(),
            uefi_boot_label: "debian".to_string(),
            default_filesystem: "ext4".to_string(),
            default_version: "12.8.0".to_string(),
            iso_download_url: "https://cdimage.debian.org/debian-cd/current/amd64/iso-cd/debian-12.8.0-amd64-netinst.iso".to_string(),
            checksum_url: "https://cdimage.debian.org/debian-cd/current/amd64/iso-cd/SHA256SUMS".to_string(),
            unattended_mechanism: "Debian Preseed (preseed.cfg)".to_string(),
        },
    ]
}

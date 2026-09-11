use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PartitionInfo {
    pub partition_number: u32,
    pub partition_guid: Option<String>,
    pub gpt_type_guid: Option<String>,
    pub starting_offset: u64,
    pub size_bytes: u64,
    pub drive_letter: Option<String>,
    pub is_boot_or_system: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VolumeInfo {
    pub drive_letter: String,
    pub volume_guid: Option<String>,
    pub label: String,
    pub filesystem: String,
    pub total_bytes: u64,
    pub free_bytes: u64,
    pub physical_disk_number: u32,
    pub partition_number: Option<u32>,
    pub is_system_volume: bool,
    pub is_ntfs: bool,
    pub bitlocker_protection: Option<String>,
    pub min_shrink_size_bytes: Option<u64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PhysicalDiskInfo {
    pub disk_number: u32,
    pub friendly_name: String,
    pub serial_number: String,
    pub bus_type: String,
    pub total_bytes: u64,
    pub partition_style: String, // "GPT" or "MBR"
    pub disk_guid: Option<String>,
    pub is_system_disk: bool,
    pub partitions: Vec<PartitionInfo>,
    pub volumes: Vec<String>, // Drive letters located on this disk
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProtectedSystemDisk {
    pub disk_number: u32,
    pub disk_guid: String,
    pub disk_serial: String,
    pub system_volume: String, // e.g. "C:"
    pub status: String,        // "PROTECTED_SYSTEM_DISK - CANNOT BE MODIFIED"
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CandidateTarget {
    pub disk_number: u32,
    pub disk_guid: String,
    pub disk_serial: String,
    pub drive_letter: String,
    pub volume_label: String,
    pub total_bytes: u64,
    pub free_bytes: u64,
    pub min_supported_bytes: Option<u64>,
    pub max_shrink_bytes: u64,
    pub is_eligible: bool,
    pub eligibility_reason: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FirmwareInfo {
    pub is_uefi: bool,
    pub firmware_type: String,
    pub secure_boot_enabled: Option<bool>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WslStatusInfo {
    pub is_installed: bool,
    pub wsl_version: Option<u32>,
    pub default_distro: Option<String>,
    pub installed_distros: Vec<String>,
    pub has_debian_helper: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DiscoveryReport {
    pub timestamp: String,
    pub firmware: FirmwareInfo,
    pub wsl: WslStatusInfo,
    pub protected_system_disk: ProtectedSystemDisk,
    pub candidate_targets: Vec<CandidateTarget>,
    pub disks: Vec<PhysicalDiskInfo>,
    pub volumes: Vec<VolumeInfo>,
}

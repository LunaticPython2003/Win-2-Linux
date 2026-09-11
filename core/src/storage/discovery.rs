use super::models::*;
use crate::system::firmware::FirmwareDetector;
use crate::system::wsl::WslDetector;
use anyhow::{Context, Result};
use chrono::Utc;
use serde::Deserialize;
use std::process::Command;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct PsDisk {
    pub number: u32,
    pub friendly_name: Option<String>,
    pub serial_number: Option<String>,
    pub bus_type: Option<String>,
    pub partition_style: Option<String>,
    pub guid: Option<String>,
    pub size: Option<u64>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct PsPartition {
    pub disk_number: u32,
    pub partition_number: u32,
    pub drive_letter: Option<String>,
    pub size: Option<u64>,
    pub gpt_type: Option<String>,
    pub guid: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "PascalCase")]
struct PsVolume {
    pub drive_letter: Option<String>,
    pub file_system_label: Option<String>,
    pub file_system: Option<String>,
    pub size_remaining: Option<u64>,
    pub size: Option<u64>,
}

pub struct StorageDiscovery;

impl StorageDiscovery {
    pub fn run_discovery() -> Result<DiscoveryReport> {
        let system_drive = std::env::var("SystemDrive")
            .unwrap_or_else(|_| "C:".to_string())
            .trim_end_matches(['\\', '/'])
            .to_uppercase();
        let system_letter = system_drive.trim_end_matches(':');

        let raw_disks = Self::query_ps_disks()?;
        let raw_partitions = Self::query_ps_partitions()?;
        let raw_volumes = Self::query_ps_volumes()?;

        // Identify which disk contains %SystemDrive%
        let system_disk_number = raw_partitions
            .iter()
            .find(|p| {
                p.drive_letter
                    .as_deref()
                    .map(|l| l.eq_ignore_ascii_case(system_letter))
                    .unwrap_or(false)
            })
            .map(|p| p.disk_number)
            .unwrap_or(0);

        let mut disks = Vec::new();
        let mut protected_system_disk = ProtectedSystemDisk {
            disk_number: system_disk_number,
            disk_guid: String::new(),
            disk_serial: String::new(),
            system_volume: format!("{}:", system_letter),
            status: "PROTECTED_SYSTEM_DISK - CANNOT BE MODIFIED".to_string(),
        };

        for d in &raw_disks {
            let is_system = d.number == system_disk_number;
            let disk_guid = d.guid.clone().unwrap_or_default().trim_matches(['{', '}']).to_string();
            let disk_serial = d.serial_number.clone().unwrap_or_default().trim().to_string();

            if is_system {
                protected_system_disk.disk_guid = disk_guid.clone();
                protected_system_disk.disk_serial = disk_serial.clone();
            }

            // Find partitions belonging to this disk
            let disk_partitions: Vec<PartitionInfo> = raw_partitions
                .iter()
                .filter(|p| p.disk_number == d.number)
                .map(|p| {
                    let letter = p.drive_letter.as_deref().map(|l| format!("{}:", l));
                    let is_boot = letter
                        .as_deref()
                        .map(|l| l.eq_ignore_ascii_case(&protected_system_disk.system_volume))
                        .unwrap_or(false);

                    PartitionInfo {
                        partition_number: p.partition_number,
                        partition_guid: p.guid.clone().map(|g| g.trim_matches(['{', '}']).to_string()),
                        gpt_type_guid: p.gpt_type.clone().map(|g| g.trim_matches(['{', '}']).to_string()),
                        starting_offset: 0,
                        size_bytes: p.size.unwrap_or(0),
                        drive_letter: letter,
                        is_boot_or_system: is_boot,
                    }
                })
                .collect();

            let volumes_on_disk: Vec<String> = disk_partitions
                .iter()
                .filter_map(|p| p.drive_letter.clone())
                .collect();

            disks.push(PhysicalDiskInfo {
                disk_number: d.number,
                friendly_name: d.friendly_name.clone().unwrap_or_else(|| "Unknown Disk".into()),
                serial_number: disk_serial,
                bus_type: d.bus_type.clone().unwrap_or_else(|| "Unknown".into()),
                total_bytes: d.size.unwrap_or(0),
                partition_style: d.partition_style.clone().unwrap_or_else(|| "Unknown".into()),
                disk_guid: Some(disk_guid),
                is_system_disk: is_system,
                partitions: disk_partitions,
                volumes: volumes_on_disk,
            });
        }

        // Parse volumes
        let mut volumes = Vec::new();
        for v in &raw_volumes {
            if let Some(letter) = &v.drive_letter {
                let drive_str = format!("{}:", letter);
                let is_sys = drive_str.eq_ignore_ascii_case(&protected_system_disk.system_volume);

                // Find backing disk number
                let disk_no = raw_partitions
                    .iter()
                    .find(|p| {
                        p.drive_letter
                            .as_deref()
                            .map(|l| l.eq_ignore_ascii_case(letter))
                            .unwrap_or(false)
                    })
                    .map(|p| p.disk_number)
                    .unwrap_or(999);

                let part_no = raw_partitions
                    .iter()
                    .find(|p| {
                        p.drive_letter
                            .as_deref()
                            .map(|l| l.eq_ignore_ascii_case(letter))
                            .unwrap_or(false)
                    })
                    .map(|p| p.partition_number);

                let fs = v.file_system.clone().unwrap_or_default();
                let is_ntfs = fs.eq_ignore_ascii_case("NTFS");

                volumes.push(VolumeInfo {
                    drive_letter: drive_str,
                    volume_guid: None,
                    label: v.file_system_label.clone().unwrap_or_default(),
                    filesystem: fs,
                    total_bytes: v.size.unwrap_or(0),
                    free_bytes: v.size_remaining.unwrap_or(0),
                    physical_disk_number: disk_no,
                    partition_number: part_no,
                    is_system_volume: is_sys,
                    is_ntfs,
                    bitlocker_protection: None,
                    min_shrink_size_bytes: None,
                });
            }
        }

        // Find candidate targets (Strictly on secondary disks != protected_system_disk)
        let mut candidate_targets = Vec::new();
        for vol in &volumes {
            if vol.is_system_volume || vol.physical_disk_number == protected_system_disk.disk_number {
                continue; // HARD EXCLUSION: Never target the system disk
            }

            let disk = disks.iter().find(|d| d.disk_number == vol.physical_disk_number);
            let disk_guid = disk.and_then(|d| d.disk_guid.clone()).unwrap_or_default();
            let disk_serial = disk.map(|d| d.serial_number.clone()).unwrap_or_default();

            // Reserve at least 20 GB buffer for Windows files
            let safety_buffer = 20 * 1024 * 1024 * 1024;
            let max_shrink = vol.free_bytes.saturating_sub(safety_buffer);
            let has_enough_space = max_shrink >= 25 * 1024 * 1024 * 1024; // At least 25 GB for Linux

            let is_eligible = vol.is_ntfs && has_enough_space;
            let eligibility_reason = if !vol.is_ntfs {
                format!("Volume is {} (only NTFS is supported for resizing)", vol.filesystem)
            } else if !has_enough_space {
                format!(
                    "Insufficient free space ({} GB available, need at least 45 GB total free space to preserve Windows safety buffer)",
                    vol.free_bytes / (1024 * 1024 * 1024)
                )
            } else {
                "Eligible for dual-boot partition creation".to_string()
            };

            candidate_targets.push(CandidateTarget {
                disk_number: vol.physical_disk_number,
                disk_guid,
                disk_serial,
                drive_letter: vol.drive_letter.clone(),
                volume_label: vol.label.clone(),
                total_bytes: vol.total_bytes,
                free_bytes: vol.free_bytes,
                min_supported_bytes: None,
                max_shrink_bytes: max_shrink,
                is_eligible,
                eligibility_reason,
            });
        }

        let firmware = FirmwareDetector::detect();
        let wsl = WslDetector::detect();

        Ok(DiscoveryReport {
            timestamp: Utc::now().to_rfc3339(),
            firmware,
            wsl,
            protected_system_disk,
            candidate_targets,
            disks,
            volumes,
        })
    }

    fn query_ps_disks() -> Result<Vec<PsDisk>> {
        let output = Command::new("powershell")
            .args([
                "-NoProfile",
                "-Command",
                "Get-Disk | Select-Object Number, FriendlyName, SerialNumber, BusType, PartitionStyle, Guid, Size | ConvertTo-Json",
            ])
            .output()
            .context("Failed to execute PowerShell Get-Disk")?;

        let text = String::from_utf8_lossy(&output.stdout).trim().to_string();
        if text.is_empty() {
            return Ok(Vec::new());
        }

        if text.starts_with('[') {
            serde_json::from_str(&text).context("Failed to deserialize disk array")
        } else {
            let single: PsDisk = serde_json::from_str(&text).context("Failed to deserialize single disk")?;
            Ok(vec![single])
        }
    }

    fn query_ps_partitions() -> Result<Vec<PsPartition>> {
        let output = Command::new("powershell")
            .args([
                "-NoProfile",
                "-Command",
                "Get-Partition | Select-Object DiskNumber, PartitionNumber, DriveLetter, Size, GptType, Guid | ConvertTo-Json",
            ])
            .output()
            .context("Failed to execute PowerShell Get-Partition")?;

        let text = String::from_utf8_lossy(&output.stdout).trim().to_string();
        if text.is_empty() {
            return Ok(Vec::new());
        }

        if text.starts_with('[') {
            serde_json::from_str(&text).context("Failed to deserialize partition array")
        } else {
            let single: PsPartition = serde_json::from_str(&text).context("Failed to deserialize single partition")?;
            Ok(vec![single])
        }
    }

    fn query_ps_volumes() -> Result<Vec<PsVolume>> {
        let output = Command::new("powershell")
            .args([
                "-NoProfile",
                "-Command",
                "Get-Volume | Select-Object DriveLetter, FileSystemLabel, FileSystem, SizeRemaining, Size | ConvertTo-Json",
            ])
            .output()
            .context("Failed to execute PowerShell Get-Volume")?;

        let text = String::from_utf8_lossy(&output.stdout).trim().to_string();
        if text.is_empty() {
            return Ok(Vec::new());
        }

        if text.starts_with('[') {
            serde_json::from_str(&text).context("Failed to deserialize volume array")
        } else {
            let single: PsVolume = serde_json::from_str(&text).context("Failed to deserialize single volume")?;
            Ok(vec![single])
        }
    }
}

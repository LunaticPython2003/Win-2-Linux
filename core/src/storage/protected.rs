use super::models::ProtectedSystemDisk;
use anyhow::{bail, Result};

pub struct SafetyValidator;

impl SafetyValidator {
    /// Strictly verifies that a proposed target disk is NOT the Windows system disk.
    /// Fails closed if the disk number, disk GUID, or disk serial matches the protected system disk.
    pub fn assert_not_protected_system_disk(
        target_disk_number: u32,
        target_disk_guid: &str,
        target_disk_serial: &str,
        protected: &ProtectedSystemDisk,
    ) -> Result<()> {
        // Invariant 1: Disk number match check
        if target_disk_number == protected.disk_number {
            bail!(
                "CRITICAL SAFETY VIOLATION: Target disk number ({}) matches the protected Windows system disk ({}). Operation ABORTED.",
                target_disk_number,
                protected.disk_number
            );
        }

        // Invariant 2: Disk GUID match check (authoritative identity)
        if !target_disk_guid.is_empty()
            && target_disk_guid.eq_ignore_ascii_case(&protected.disk_guid)
        {
            bail!(
                "CRITICAL SAFETY VIOLATION: Target disk GUID ({}) matches protected Windows system disk GUID ({}). Operation ABORTED.",
                target_disk_guid,
                protected.disk_guid
            );
        }

        // Invariant 3: Disk serial match check
        if !target_disk_serial.is_empty()
            && target_disk_serial.eq_ignore_ascii_case(&protected.disk_serial)
        {
            bail!(
                "CRITICAL SAFETY VIOLATION: Target disk serial number ({}) matches protected Windows system disk serial ({}). Operation ABORTED.",
                target_disk_serial,
                protected.disk_serial
            );
        }

        Ok(())
    }

    /// Verifies that a target drive letter is not the protected Windows system volume (%SystemDrive% / C:)
    pub fn assert_not_system_volume(
        target_drive_letter: &str,
        protected: &ProtectedSystemDisk,
    ) -> Result<()> {
        let clean_target = target_drive_letter.trim_end_matches(['\\', '/']).to_uppercase();
        let clean_system = protected.system_volume.trim_end_matches(['\\', '/']).to_uppercase();

        if clean_target == clean_system {
            bail!(
                "CRITICAL SAFETY VIOLATION: Target volume '{}' is the protected Windows system volume '{}'. Operation ABORTED.",
                target_drive_letter,
                protected.system_volume
            );
        }

        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_rejects_matching_disk_number() {
        let protected = ProtectedSystemDisk {
            disk_number: 0,
            disk_guid: "1111-2222-3333-4444".into(),
            disk_serial: "SERIAL-SYS".into(),
            system_volume: "C:".into(),
            status: "PROTECTED_SYSTEM_DISK".into(),
        };

        let result = SafetyValidator::assert_not_protected_system_disk(
            0,
            "5555-6666-7777-8888",
            "SERIAL-SEC",
            &protected,
        );
        assert!(result.is_err());
    }

    #[test]
    fn test_rejects_matching_guid() {
        let protected = ProtectedSystemDisk {
            disk_number: 0,
            disk_guid: "1111-2222-3333-4444".into(),
            disk_serial: "SERIAL-SYS".into(),
            system_volume: "C:".into(),
            status: "PROTECTED_SYSTEM_DISK".into(),
        };

        let result = SafetyValidator::assert_not_protected_system_disk(
            1,
            "1111-2222-3333-4444",
            "SERIAL-SEC",
            &protected,
        );
        assert!(result.is_err());
    }

    #[test]
    fn test_rejects_matching_serial() {
        let protected = ProtectedSystemDisk {
            disk_number: 0,
            disk_guid: "1111-2222-3333-4444".into(),
            disk_serial: "SERIAL-SYS".into(),
            system_volume: "C:".into(),
            status: "PROTECTED_SYSTEM_DISK".into(),
        };

        let result = SafetyValidator::assert_not_protected_system_disk(
            1,
            "5555-6666-7777-8888",
            "SERIAL-SYS",
            &protected,
        );
        assert!(result.is_err());
    }

    #[test]
    fn test_accepts_valid_secondary_disk() {
        let protected = ProtectedSystemDisk {
            disk_number: 0,
            disk_guid: "1111-2222-3333-4444".into(),
            disk_serial: "SERIAL-SYS".into(),
            system_volume: "C:".into(),
            status: "PROTECTED_SYSTEM_DISK".into(),
        };

        let result = SafetyValidator::assert_not_protected_system_disk(
            1,
            "5555-6666-7777-8888",
            "SERIAL-SEC",
            &protected,
        );
        assert!(result.is_ok());
    }
}

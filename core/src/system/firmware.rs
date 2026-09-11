use crate::storage::models::FirmwareInfo;
use std::process::Command;
use windows::Win32::System::SystemInformation::{GetFirmwareType, FIRMWARE_TYPE};

pub struct FirmwareDetector;

impl FirmwareDetector {
    pub fn detect() -> FirmwareInfo {
        let mut fw_type = FIRMWARE_TYPE(0);
        let win_api_ok = unsafe { GetFirmwareType(&mut fw_type).is_ok() };

        let (is_uefi, firmware_type_str) = if win_api_ok {
            match fw_type.0 {
                1 => (false, "Legacy BIOS (FIRMWARE_TYPE_BIOS)".to_string()),
                2 => (true, "UEFI (FIRMWARE_TYPE_UEFI)".to_string()),
                _ => (false, format!("Unknown ({})", fw_type.0)),
            }
        } else {
            // Fallback via PowerShell
            Self::detect_via_powershell()
        };

        let secure_boot = Self::check_secure_boot(is_uefi);

        FirmwareInfo {
            is_uefi,
            firmware_type: firmware_type_str,
            secure_boot_enabled: secure_boot,
        }
    }

    fn detect_via_powershell() -> (bool, String) {
        let output = Command::new("powershell")
            .args(["-NoProfile", "-Command", "Confirm-SystemFirmwareType"])
            .output();

        if let Ok(out) = output {
            let text = String::from_utf8_lossy(&out.stdout).trim().to_uppercase();
            if text.contains("UEFI") {
                return (true, "UEFI".to_string());
            } else if text.contains("BIOS") {
                return (false, "Legacy BIOS".to_string());
            }
        }
        (false, "Unknown".to_string())
    }

    fn check_secure_boot(is_uefi: bool) -> Option<bool> {
        if !is_uefi {
            return Some(false);
        }

        let output = Command::new("powershell")
            .args(["-NoProfile", "-Command", "Confirm-SecureBootUEFI -ErrorAction SilentlyContinue"])
            .output();

        if let Ok(out) = output {
            let text = String::from_utf8_lossy(&out.stdout).trim().to_uppercase();
            if text == "TRUE" {
                return Some(true);
            } else if text == "FALSE" {
                return Some(false);
            }
        }

        None
    }
}

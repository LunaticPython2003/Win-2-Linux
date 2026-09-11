use crate::storage::models::WslStatusInfo;
use std::process::Command;

pub struct WslDetector;

impl WslDetector {
    pub fn detect() -> WslStatusInfo {
        let output = Command::new("wsl")
            .args(["--status"])
            .output();

        let is_installed = match &output {
            Ok(out) => out.status.success() || !out.stdout.is_empty(),
            Err(_) => false,
        };

        if !is_installed {
            return WslStatusInfo {
                is_installed: false,
                wsl_version: None,
                default_distro: None,
                installed_distros: vec![],
                has_debian_helper: false,
            };
        }

        // List distributions
        let list_output = Command::new("wsl")
            .args(["-l", "-v"])
            .output();

        let mut installed_distros = Vec::new();
        let mut default_distro = None;
        let mut has_debian = false;

        if let Ok(out) = list_output {
            // wsl output on Windows is UTF-16LE, let's decode cleanly
            let text = Self::decode_utf16_or_utf8(&out.stdout);
            for line in text.lines().skip(1) {
                let trimmed = line.trim();
                if trimmed.is_empty() {
                    continue;
                }
                let is_default = trimmed.starts_with('*');
                let clean = trimmed.trim_start_matches('*').trim();
                let parts: Vec<&str> = clean.split_whitespace().collect();
                if let Some(&name) = parts.first() {
                    if !name.is_empty() {
                        installed_distros.push(name.to_string());
                        if is_default {
                            default_distro = Some(name.to_string());
                        }
                        if name.eq_ignore_ascii_case("Debian") {
                            has_debian = true;
                        }
                    }
                }
            }
        }

        WslStatusInfo {
            is_installed: true,
            wsl_version: Some(2),
            default_distro,
            installed_distros,
            has_debian_helper: has_debian,
        }
    }

    /// WSL outputs UTF-16LE bytes with null padding on Windows
    fn decode_utf16_or_utf8(bytes: &[u8]) -> String {
        if bytes.len() >= 2 && (bytes[1] == 0 || bytes[0] == 0xff && bytes[1] == 0xfe) {
            let u16_slice: Vec<u16> = bytes
                .chunks_exact(2)
                .map(|chunk| u16::from_le_bytes([chunk[0], chunk[1]]))
                .collect();
            String::from_utf16_lossy(&u16_slice)
        } else {
            String::from_utf8_lossy(bytes).to_string()
        }
    }
}

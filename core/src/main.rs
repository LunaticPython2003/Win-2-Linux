mod distro;
mod storage;
mod system;

use anyhow::Result;
use clap::{Parser, Subcommand};
use distro::get_supported_distributions;
use storage::discovery::StorageDiscovery;
use storage::protected::SafetyValidator;

#[derive(Parser)]
#[command(name = "win2linux-core")]
#[command(about = "Safe Windows-to-Linux Dual-Boot Storage & Boot Orchestrator", long_about = None)]
struct Cli {
    #[command(subcommand)]
    command: Commands,
}

#[derive(Subcommand)]
enum Commands {
    /// Perform safe read-only discovery of storage, UEFI firmware, and WSL
    Discover {
        /// Output results as raw JSON for GUI consumption
        #[arg(short, long)]
        json: bool,
    },
    /// List supported Secure Boot certified Linux distributions
    Distros {
        /// Output as raw JSON
        #[arg(short, long)]
        json: bool,
    },
    /// Run safety invariant validation against the protected system disk
    CheckSafety {
        #[arg(short, long)]
        target_disk: u32,
        #[arg(short, long)]
        target_guid: String,
        #[arg(short, long)]
        target_serial: String,
        #[arg(short, long)]
        target_volume: String,
    },
}

fn main() -> Result<()> {
    let cli = Cli::parse();

    match cli.command {
        Commands::Discover { json } => {
            let report = StorageDiscovery::run_discovery()?;

            if json {
                println!("{}", serde_json::to_string_pretty(&report)?);
            } else {
                println!("============================================================");
                println!("      Win2Linux — Safe Preflight Storage Discovery          ");
                println!("============================================================");
                println!();

                // Firmware
                println!("FIRMWARE & SECURITY:");
                println!("  Boot Mode:       {}", report.firmware.firmware_type);
                let sb_str = match report.firmware.secure_boot_enabled {
                    Some(true) => "ENABLED (Secure Boot Active)",
                    Some(false) => "DISABLED",
                    None => "UNKNOWN / NOT QUERIED",
                };
                println!("  Secure Boot:     {}", sb_str);
                println!();

                // WSL
                println!("WSL HELPER ENVIRONMENT:");
                println!("  WSL Installed:   {}", if report.wsl.is_installed { "YES" } else { "NO" });
                if let Some(def) = &report.wsl.default_distro {
                    println!("  Default Distro:  {}", def);
                }
                println!("  Distros:         {:?}", report.wsl.installed_distros);
                println!("  Debian Helper:   {}", if report.wsl.has_debian_helper { "READY" } else { "NOT INSTALLED (will be provisioned)" });
                println!();

                // Protected System Disk
                println!("PROTECTED WINDOWS SYSTEM DISK (NON-MODIFIABLE):");
                println!("  Disk Number:     Disk {}", report.protected_system_disk.disk_number);
                println!("  Volume:          {}", report.protected_system_disk.system_volume);
                println!("  Disk Serial:     {}", report.protected_system_disk.disk_serial);
                println!("  Disk GUID:       {}", report.protected_system_disk.disk_guid);
                println!("  Status:          🔒 {}", report.protected_system_disk.status);
                println!();

                // Target Candidates
                println!("ELIGIBLE SECONDARY TARGETS (CAN SAFELY BE SHRUNK):");
                if report.candidate_targets.is_empty() {
                    println!("  [!] No eligible secondary targets found.");
                } else {
                    for target in &report.candidate_targets {
                        let total_gb = target.total_bytes / (1024 * 1024 * 1024);
                        let free_gb = target.free_bytes / (1024 * 1024 * 1024);
                        let max_shrink_gb = target.max_shrink_bytes / (1024 * 1024 * 1024);

                        println!("  Drive {}: [{}] on Disk {}", target.drive_letter, target.volume_label, target.disk_number);
                        println!("    Total Size:    {} GB", total_gb);
                        println!("    Free Space:    {} GB", free_gb);
                        println!("    Max For Linux: {} GB (preserving 20 GB Windows buffer)", max_shrink_gb);
                        println!("    Eligibility:   {}", if target.is_eligible { "ELIGIBLE" } else { "INELIGIBLE" });
                        println!("    Details:       {}", target.eligibility_reason);
                        println!();
                    }
                }

                // All Physical Disks
                println!("PHYSICAL DISKS INVENTORY:");
                for d in &report.disks {
                    let lock_symbol = if d.is_system_disk { "🔒 [PROTECTED]" } else { "✓ [SECONDARY]" };
                    let size_gb = d.total_bytes / (1024 * 1024 * 1024);
                    println!("  Disk {}: {} ({} GB, {}) {}", d.disk_number, d.friendly_name, size_gb, d.bus_type, lock_symbol);
                    println!("    Serial:        {}", d.serial_number);
                    if let Some(g) = &d.disk_guid {
                        println!("    GPT GUID:      {}", g);
                    }
                    println!("    Volumes:       {:?}", d.volumes);
                    println!();
                }
            }
        }
        Commands::Distros { json } => {
            let distros = get_supported_distributions();
            if json {
                println!("{}", serde_json::to_string_pretty(&distros)?);
            } else {
                println!("============================================================");
                println!("   Secure-Boot Certified Supported Linux Distributions      ");
                println!("============================================================");
                println!();
                for d in distros {
                    println!("* {} (ID: {})", d.display_name, d.id);
                    println!("    Description:        {}", d.description);
                    println!("    Secure Boot:        {} via {}", d.secure_boot_status, d.secure_boot_signer);
                    println!("    Default Filesystem: {}", d.default_filesystem);
                    println!("    EFI Vendor Dir:     \\EFI\\{}\\ {}", d.efi_vendor_dir, d.efi_binary);
                    println!("    UEFI NVRAM Label:   {}", d.uefi_boot_label);
                    println!("    Unattended Install: {}", d.unattended_mechanism);
                    println!();
                }
            }
        }
        Commands::CheckSafety {
            target_disk,
            target_guid,
            target_serial,
            target_volume,
        } => {
            let report = StorageDiscovery::run_discovery()?;
            SafetyValidator::assert_not_protected_system_disk(
                target_disk,
                &target_guid,
                &target_serial,
                &report.protected_system_disk,
            )?;
            SafetyValidator::assert_not_system_volume(
                &target_volume,
                &report.protected_system_disk,
            )?;
            println!("SAFETY CHECK PASSED: Target Disk {} ({}) is verified safe and isolated from Windows system disk.", target_disk, target_volume);
        }
    }

    Ok(())
}

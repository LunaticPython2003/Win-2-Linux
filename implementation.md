# Windows-to-Linux Installer --- Implementation Specification

## 1. Project goal

Build a Windows application, implemented primarily in **Rust or Go**,
that provides a Wubi-like experience:

1.  Run entirely from Windows initially.
2.  Install a lightweight Linux distribution into **WSL** as a local
    Linux-side helper environment.
3.  Detect the machine's storage, UEFI, Secure Boot, BitLocker, and
    Windows volumes.
4.  Allow the user to reserve a selected amount of space from an
    existing Windows volume such as `D:`.
5.  Safely shrink the NTFS volume using Windows-supported storage
    functionality.
6.  Leave the resulting space as **unallocated physical disk space**.
7.  On reboot, boot a small Linux installer environment.
8.  Install **Ubuntu** into the newly-created physical Linux
    partition(s).
9.  Install Ubuntu's Secure-Boot-compatible EFI boot chain.
10. Create a real UEFI NVRAM boot entry named `ubuntu`.
11. Preserve the existing `Windows Boot Manager` entry.
12. Reboot into a normal, independent Ubuntu installation.
13. Never depend on WSL for the final Linux boot/runtime environment.

The target result is a conventional dual-boot installation, not WSL, a
VM, or a Linux filesystem stored inside an NTFS file.

------------------------------------------------------------------------



# 2.1 Windows system SSD must be protected

This is a **hard product requirement**.

The SSD containing the Windows operating-system installation must be treated as **read-only from the perspective of partition/layout modification**, except for explicitly documented boot configuration operations that are strictly required and independently verified.

The normal installation target is a **secondary physical SSD** containing a Windows volume such as `D:`.

Example:

```text
Physical Disk 0 — WINDOWS SYSTEM SSD
+---------------------------------------------+
| EFI System Partition                        |
| Microsoft Reserved                           |
| C: Windows                                   |
| Windows Recovery                             |
+---------------------------------------------+

Physical Disk 1 — SECONDARY SSD
+---------------------------------------------+
| D: NTFS                                     |
| existing user data                           |
|                                             |
| <-- shrink this volume only -->              |
|                                             |
| Linux target space                           |
+---------------------------------------------+
```

The installer MUST NOT:

- shrink `C:`
- resize any partition on the Windows system SSD
- create Linux partitions on the Windows system SSD
- format any partition on the Windows system SSD
- delete any partition on the Windows system SSD
- move Windows partitions
- convert the Windows system disk between partitioning schemes
- repartition the Windows system disk
- use the Windows system disk as the Linux root filesystem

The installer should identify the Windows system disk explicitly during preflight and mark it as:

```text
PROTECTED_SYSTEM_DISK
```

The storage engine must reject any destructive operation whose target disk is the protected system disk.

---

# 2.1.1 Determining the protected Windows system disk

Do not assume:

```text
Disk 0 = Windows disk
```

Instead, determine which physical disk contains the currently-running Windows installation.

Use Windows-supported storage/system APIs to identify the physical disk backing:

```text
%SystemDrive%
```

normally:

```text
C:
```

and trace that volume to its physical disk.

Record durable identifiers:

```text
system_disk_guid
system_disk_serial
system_disk_number
system_partition_guids
system_volume_guid
```

The physical disk's stable identity, not merely its Windows disk number, is the authoritative identifier.

Example persisted state:

```json
{
  "protected_system_disk": {
    "disk_guid": "SYSTEM-DISK-GUID",
    "disk_serial": "SYSTEM-DISK-SERIAL",
    "system_volume": "C:"
  },
  "linux_target": {
    "disk_guid": "SECONDARY-DISK-GUID",
    "disk_serial": "SECONDARY-DISK-SERIAL",
    "windows_volume": "D:"
  }
}
```

Before **every** destructive storage operation, independently re-check that:

```text
operation.target_disk_guid != protected_system_disk.disk_guid
```

If they match:

```text
ABORT
```

Do not attempt to recover by guessing another disk.

---

# 2.1.2 Preferred physical topology

The supported v1 topology should be:

```text
Disk A
└── Windows
    ├── EFI
    ├── C:
    └── Recovery

Disk B
└── D:
    └── existing data

        ↓ installer shrinks D:

Disk A
└── Windows
    ├── EFI
    ├── C:
    └── Recovery

Disk B
├── D:
└── Linux
```

This provides a clean separation between:

```text
Windows system disk
```

and:

```text
Linux installation disk
```

---

# 2.1.3 UEFI exception

There is one important nuance.

The Windows system SSD may contain the machine's existing EFI System Partition (ESP), and Windows Boot Manager normally resides there.

The installer should **prefer a dedicated EFI System Partition on the secondary Linux SSD** when the product's supported installation layout permits it.

This allows the Linux installation to be as independent as possible:

```text
Windows SSD
└── Windows EFI
    └── EFI/Microsoft/

Secondary SSD
└── Linux EFI
    └── EFI/ubuntu/
```

The final UEFI configuration can then point the `ubuntu` boot entry to the secondary SSD's ESP.

The Windows SSD remains untouched by partition operations.

If the implementation needs to modify an existing Windows SSD ESP for a specific supported configuration, that must be treated as a separate, explicitly documented operation and must **never** involve repartitioning or modifying Windows filesystem/partition contents unnecessarily.

For v1, prefer avoiding the Windows SSD entirely whenever possible.

---

# 2.1.4 Target selection UI

The GUI must visually distinguish the protected Windows disk.

Example:

```text
Storage

┌──────────────────────────────────────────────┐
│ 🔒 DISK 0 — Windows System Disk              │
│ 1 TB NVMe                                    │
│                                              │
│ C: Windows                                   │
│                                              │
│ PROTECTED — cannot be selected               │
└──────────────────────────────────────────────┘


┌──────────────────────────────────────────────┐
│ DISK 1 — Secondary SSD                       │
│ 1 TB NVMe                                    │
│                                              │
│ D: Data                                      │
│ 650 GB free                                  │
│                                              │
│ [ Select ]                                   │
└──────────────────────────────────────────────┘
```

The protected disk must not merely be visually disabled.

The backend/service must enforce the restriction independently.

A malicious or buggy GUI must not be able to request:

```text
shrink(system_disk)
```

from the privileged service.

---

# 2.1.5 Final invariant

The following invariant must hold throughout the entire installation transaction:

```text
LINUX_TARGET_DISK != WINDOWS_SYSTEM_DISK
```

If this invariant ever becomes false:

```text
STOP INSTALLATION IMMEDIATELY
```

The installer must fail closed.

The application must never "pick the next disk" or attempt to infer the user's intent after an identity mismatch.

# 2. Critical design principles

## 2.1 Do not implement filesystem manipulation from scratch

The application must NOT implement its own NTFS shrinker, GPT parser,
ext4 formatter, or UEFI variable implementation unless there is an
unavoidable reason.

Use mature operating-system facilities and Linux utilities.

The dangerous path should be:

``` text
Windows storage APIs
    -> Windows performs NTFS shrink
    -> Linux installer sees unallocated space
    -> Linux tools create Linux partitions/filesystems
    -> Ubuntu's boot tooling creates EFI boot files/NVRAM entry
```

Do not create custom code such as:

``` text
my_ntfs_shrinker()
my_ext4_formatter()
my_gpt_writer()
my_uefi_nvrm_writer()
```

The application is an **orchestrator**, not a replacement for Windows
Disk Management or Linux installation tooling.

------------------------------------------------------------------------

# 3. Recommended technology choice

## Primary recommendation: Rust

Rust is preferred for the Windows application because the project
performs privileged and potentially destructive operations.

Suggested stack:

-   Rust
-   `windows` crate for Windows APIs
-   `serde` / `serde_json`
-   `reqwest` or equivalent for downloads
-   `sha2` for hashes
-   `ed25519-dalek` or equivalent only if needed for application
    metadata/signatures
-   `tokio` if asynchronous operations are needed
-   WinUI 3 / native Windows frontend, or a separate webview frontend
-   Windows Service for privileged operations

Go is acceptable, but the storage/Windows API layer should still be
carefully isolated.

------------------------------------------------------------------------

# 4. High-level architecture

``` text
+-------------------------------------------------------+
|                 Windows GUI                           |
|                                                       |
|  Distribution: Ubuntu                                 |
|  Target volume: D:                                    |
|  Linux size: 500 GB                                   |
|                                                       |
|                [ Install ]                            |
+-------------------------+-----------------------------+
                          |
                          v
+-------------------------------------------------------+
|              Installer Controller                     |
|                                                       |
|  - system discovery                                   |
|  - safety checks                                      |
|  - download manager                                   |
|  - WSL manager                                        |
|  - storage planner                                    |
|  - reboot coordinator                                 |
|  - state machine                                      |
+-------------------------+-----------------------------+
                          |
                          v
+-------------------------------------------------------+
|        Privileged Windows Installer Service           |
|                                                       |
|  - volume inspection                                  |
|  - NTFS shrink                                        |
|  - EFI/boot preparation                               |
|  - boot configuration                                 |
|  - recovery/rollback                                  |
+-------------------------+-----------------------------+
                          |
                          v
                 Windows reboot
                          |
                          v
+-------------------------------------------------------+
|              Linux Installer Environment              |
|                                                       |
|  - target disk identification                         |
|  - partition creation                                 |
|  - filesystem creation                                |
|  - Ubuntu installation                                |
|  - fstab generation                                   |
|  - initramfs                                          |
|  - Secure Boot bootloader                             |
|  - efibootmgr / UEFI NVRAM                            |
|  - installation verification                          |
+-------------------------+-----------------------------+
                          |
                          v
                       reboot
                          |
                          v
                 Ubuntu / GRUB / Windows
```

------------------------------------------------------------------------

# 5. WSL requirement

The first-run Windows application must provision a lightweight WSL Linux
environment.

This environment is a **development/helper environment**, not the final
boot environment.

## Recommended WSL distribution

Use **Debian** unless there is a strong reason to use another
distribution.

Reasons:

-   lightweight
-   stable
-   readily available for WSL
-   good package availability
-   easy scripting
-   sufficient for image/archive manipulation and Linux-side preparation

Ubuntu WSL is also acceptable and may simplify Ubuntu-specific tooling.

Do not confuse:

``` text
WSL Ubuntu
```

with:

``` text
Physical Ubuntu installation
```

They are independent.

------------------------------------------------------------------------

# 6. WSL bootstrap

The Windows installer should detect:

``` text
- Windows version
- WSL availability
- WSL version
- virtualization availability
- installed WSL distributions
```

If WSL is unavailable, provide a controlled installation path.

The application should use Microsoft's supported WSL command/API
mechanisms rather than modifying WSL's internal virtual disk manually.

Conceptual bootstrap:

``` text
Check WSL
   |
   +-- installed? --> continue
   |
   +-- missing --> request Administrator permission
                       |
                       v
                  enable/install WSL
                       |
                       v
                  reboot if required
```

Then install the helper distribution.

Example conceptual command:

``` powershell
wsl --install --distribution Debian
```

The implementation must not assume this exact command works on every
supported Windows release. Detect capabilities first and select the
appropriate supported installation method.

After installation:

``` text
wsl.exe -d Debian -- <command>
```

can be used to run helper operations.

------------------------------------------------------------------------

# 7. What WSL should do

WSL may be used for:

-   ISO inspection
-   checksum verification
-   archive extraction
-   generating installation metadata
-   preparing scripts
-   manipulating Linux root filesystem archives
-   preparing installer payloads
-   validating Ubuntu filesystem structures
-   building a custom minimal installer environment
-   development/testing helpers

WSL should NOT be responsible for:

-   the final Ubuntu boot
-   the final Ubuntu kernel runtime
-   writing arbitrary partitions while Windows is actively using them
-   creating the final UEFI NVRAM entry
-   assuming `/dev/nvmeX` numbering
-   converting a WSL distro directly into the final physical OS

The physical installation should happen after reboot in a Linux
environment.

------------------------------------------------------------------------

# 8. Why the installer needs a second Linux environment

WSL runs under Windows.

The final Ubuntu installation needs to become independent of Windows.

Therefore:

``` text
WSL
 |
 | helper/preparation only
 v
Windows
 |
 | reboot
 v
Linux installer environment
 |
 | install
 v
Physical Ubuntu
```

Do not try to make WSL itself become the bootloader/installer.

------------------------------------------------------------------------

# 9. Storage model

Assume:

``` text
Physical Disk 1
+---------------------------------------------+
| Windows / C: / EFI / Recovery               |
+---------------------------------------------+

Physical Disk 2
+---------------------------------------------+
| D: NTFS                                     |
|                                             |
| existing user data                          |
|                                             |
+---------------------------------------------+
```

After the Windows-side operation:

``` text
Physical Disk 2
+-----------------------------+---------------+
| D: NTFS                     | UNALLOCATED   |
| ~500 GB                     | ~500 GB       |
+-----------------------------+---------------+
```

The application must never describe this as "a partition inside D:".

A partition is a physical disk-level structure.

The operation is:

``` text
Shrink D: filesystem
    ->
Shrink D: partition
    ->
Create unallocated physical space
```

------------------------------------------------------------------------

# 10. Disk identification

Never rely solely on:

``` text
Disk 1
/dev/nvme1n1
```

because numbering can change.

Before reboot, record durable identifiers.

Recommended identifiers:

-   physical disk serial number
-   GPT disk GUID
-   target volume GUID
-   GPT partition GUID for D:
-   filesystem type
-   original partition size
-   original volume size
-   expected resulting partition size

Example state:

``` json
{
  "target": {
    "disk_guid": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
    "disk_serial": "SERIAL",
    "windows_volume": "D:",
    "volume_guid": "\\\\?\\Volume{...}",
    "original_partition_size": 1099511627776,
    "requested_linux_bytes": 536870912000
  }
}
```

After reboot, the Linux installer must locate the disk by stable
identity, not by `/dev/nvme1n1`.

------------------------------------------------------------------------

# 11. Preflight checks

Before any destructive operation, perform all of these checks.

## Firmware

Check:

``` text
UEFI boot mode
Secure Boot state
```

The project should initially support UEFI-only systems.

Do not support legacy BIOS/CSM in v1.

## Partitioning

Require:

``` text
GPT
```

Reject or explicitly handle MBR.

## Windows

Verify:

-   Windows is booting from UEFI
-   Windows volume is healthy
-   target volume is NTFS
-   enough free space exists
-   volume is not currently undergoing a conflicting operation
-   no pending restart that would make the operation unsafe

## BitLocker

Detect BitLocker.

If the target volume is encrypted, do not blindly manipulate it.

The application should either:

1.  support BitLocker safely using documented Windows mechanisms, or
2.  require the user to suspend/properly handle BitLocker before
    continuing.

Never silently disable encryption.

## Power

Require AC power on laptops where appropriate.

Display a warning against interrupting the operation.

## Backup

Require an explicit confirmation that important data is backed up.

The application cannot guarantee data preservation in the presence of
disk failure or power loss.

------------------------------------------------------------------------

# 12. NTFS shrinking

Do not implement the shrink algorithm.

Use Windows-supported volume management functionality.

Conceptual operation:

``` text
D:
  current size = 1 TB
  requested Linux space = 500 GB

Ask Windows for minimum supported volume size.

If:

minimum_size <= 500 GB equivalent target

then:

new_D_size = original_D_size - 500 GB

request Windows to shrink D:
```

The actual API selection should be researched and implemented using
supported Windows storage APIs.

The installer must not simply run an unverified command string and
assume success.

After shrinking:

1.  Re-query the disk.
2.  Verify the D: partition size.
3.  Verify the D: filesystem is accessible.
4.  Verify the target unallocated region exists.
5.  Verify the unallocated region is on the intended physical disk.
6.  Persist a transaction checkpoint.

------------------------------------------------------------------------

# 13. Transaction/state machine

The installer must be resumable.

Use a state machine such as:

``` text
INITIAL
  |
  v
PREFLIGHT_COMPLETE
  |
  v
WSL_READY
  |
  v
PAYLOAD_READY
  |
  v
DISK_IDENTIFIED
  |
  v
SHRINK_STARTED
  |
  v
SHRINK_VERIFIED
  |
  v
BOOT_ENV_PREPARED
  |
  v
REBOOT_REQUIRED
  |
  v
LINUX_INSTALLER_STARTED
  |
  v
TARGET_VERIFIED
  |
  v
PARTITIONS_CREATED
  |
  v
FILESYSTEMS_CREATED
  |
  v
UBUNTU_INSTALLED
  |
  v
EFI_FILES_INSTALLED
  |
  v
UEFI_ENTRY_CREATED
  |
  v
BOOT_VERIFIED
  |
  v
COMPLETE
```

Store state in a durable Windows-side location.

Example:

``` text
C:\ProgramData\Win2Linux\state.json
```

Never leave the state only in memory.

------------------------------------------------------------------------

# 14. Recovery philosophy

Every destructive step must have a verification checkpoint.

Example:

``` text
Before shrink
    |
    +--> save disk metadata
    |
    v
Shrink
    |
    +--> failure --> do not continue
    |
    v
Verify NTFS
    |
    +--> failure --> recovery mode
    |
    v
Continue
```

Do not attempt to automatically "repair" a potentially damaged
filesystem without first determining what happened.

------------------------------------------------------------------------

# 15. Preparing the Linux installer environment

The Windows application needs a way to boot a Linux environment after
reboot.

Preferred v1 approach:

-   prepare a small Linux installer image
-   use Ubuntu-compatible signed EFI boot components where possible
-   place required EFI files and installer payload on an appropriate
    bootable medium/partition
-   create a temporary UEFI boot path
-   reboot into the installer
-   perform the physical installation
-   remove temporary installer state after completion

The exact boot handoff mechanism should be implemented only after
validating it against the target Windows/UEFI configurations.

Do not overwrite the Windows EFI partition blindly.

------------------------------------------------------------------------

# 16. EFI System Partition

UEFI firmware boots `.efi` executables from an **EFI System Partition
(ESP)**.

Example:

``` text
ESP
|
+-- EFI
    |
    +-- Microsoft
    |    |
    |    +-- Boot
    |         |
    |         +-- bootmgfw.efi
    |
    +-- ubuntu
         |
         +-- shimx64.efi
         +-- grubx64.efi
         +-- grub.cfg
```

The Windows bootloader remains intact.

Ubuntu adds its own directory.

------------------------------------------------------------------------

# 17. Secure Boot chain

The intended Ubuntu boot chain is conceptually:

``` text
UEFI firmware
    |
    | Secure Boot verification
    v
Ubuntu/Microsoft-trusted shim
    |
    v
GRUB
    |
    v
Linux kernel
    |
    v
Ubuntu
```

Do not create a homemade unsigned EFI loader for the production
installation.

Use Ubuntu's supported Secure Boot boot components.

The implementation should verify the exact boot artifacts supplied by
the selected Ubuntu release rather than hardcoding assumptions about
filenames, architecture, or package versions.

------------------------------------------------------------------------

# 18. Creating the Linux partitions

Once booted into the Linux installer environment:

1.  Locate the target physical disk by GPT disk GUID/serial.
2.  Locate the unallocated region created by Windows.
3.  Verify that the existing D: partition's GPT GUID and size match the
    recorded transaction state.
4.  Refuse to continue if the disk topology differs unexpectedly.
5.  Create Linux partition(s) only inside the previously-created
    unallocated region.

Example:

``` text
Disk
+-----------------------+----------------------+
| D: NTFS               | Unallocated          |
|                       |                      |
+-----------------------+----------------------+
                        ^
                        |
                   ONLY USE THIS
```

Possible layout:

``` text
+-----------------------+---------+-------------+
| D: NTFS               | ESP     | Ubuntu /   |
|                       | 512MB-1G| ext4        |
+-----------------------+---------+-------------+
```

Alternatively, reuse an existing appropriate ESP after carefully
validating it.

For a standalone secondary Linux disk, a dedicated ESP on the same disk
can simplify disk independence.

------------------------------------------------------------------------

# 19. Filesystem

For Ubuntu v1, use:

``` text
ext4
```

for the root filesystem.

Avoid adding Btrfs/ZFS/etc. in v1.

Do not create a swap partition unless there is a clear product
requirement.

Ubuntu can use a swapfile.

------------------------------------------------------------------------

# 20. Installing Ubuntu

There are two viable implementation strategies.

## Strategy A --- automate Ubuntu's installer

Use Ubuntu's supported unattended installation mechanisms.

Advantages:

-   closer to official installation behavior
-   easier package configuration
-   better compatibility with Ubuntu updates
-   easier future maintenance

This should be the preferred production path.

## Strategy B --- deploy a prepared root filesystem

The Linux installer can create the filesystem and deploy an Ubuntu root
filesystem.

This is simpler in some custom installer designs but creates more
responsibility for:

-   package configuration
-   kernel packages
-   initramfs
-   machine identity
-   bootloader
-   networking
-   users
-   cloud-init/installer state

Prefer Strategy A unless there is a strong reason to deploy a custom
rootfs.

------------------------------------------------------------------------

# 21. `/etc/fstab`

After installation, verify the root filesystem is referenced by UUID.

Example concept:

``` text
UUID=<root-uuid> / ext4 defaults 0 1
```

Do not hardcode:

``` text
/dev/nvme1n1p2
```

because device names can change.

------------------------------------------------------------------------

# 22. UEFI boot registration

This is the critical BIOS/UEFI step.

UEFI firmware stores boot entries in firmware NVRAM.

Conceptual result:

``` text
Boot0000* Windows Boot Manager
Boot0001* ubuntu
```

The Ubuntu entry points to an EFI executable:

``` text
\EFI\ubuntu\shimx64.efi
```

The actual device/partition reference is encoded in the UEFI boot entry.

------------------------------------------------------------------------

# 23. Use Linux's EFI tooling for registration

The Linux installer environment should perform UEFI registration.

Typical tooling includes:

``` text
efibootmgr
grub-install
```

Do not assume that the exact command line is universal.

The installer must detect:

``` text
UEFI mode
ESP
architecture
Secure Boot state
Ubuntu bootloader package/artifact versions
```

and invoke the appropriate Ubuntu tooling.

Conceptual operation:

``` text
mount ESP
    |
    v
install Ubuntu shim + GRUB
    |
    v
create Boot#### NVRAM entry
    |
    v
set desired boot order
```

------------------------------------------------------------------------

# 24. What a UEFI boot entry represents

Conceptually:

``` text
Boot0001
  Label: ubuntu
  Device: <ESP partition>
  Path: \EFI\ubuntu\shimx64.efi
```

The firmware does NOT understand:

``` text
"this is Ubuntu"
```

as an operating system concept.

It understands:

``` text
"execute this EFI program from this filesystem"
```

That EFI program begins the Ubuntu boot chain.

------------------------------------------------------------------------

# 25. BootOrder

Do not delete:

``` text
Windows Boot Manager
```

Instead, create:

``` text
ubuntu
```

and place it appropriately in BootOrder.

Example:

``` text
BootOrder: 0001,0000

Boot0000* Windows Boot Manager
Boot0001* ubuntu
```

GRUB should contain an entry for Windows.

The user can therefore choose:

``` text
Ubuntu
Windows Boot Manager
```

from GRUB.

The application must preserve the original Windows boot entry.

------------------------------------------------------------------------

# 26. UEFI registration failure handling

If Linux installation succeeds but NVRAM registration fails:

Do NOT report "installation complete".

Instead:

``` text
Ubuntu files installed:
YES

Root filesystem:
YES

EFI files:
YES

UEFI NVRAM entry:
FAILED

Status:
RECOVERY REQUIRED
```

Attempt a documented fallback only if it is known to be safe and
supported.

UEFI firmware implementations vary, so the installer should not assume
that every machine behaves identically.

------------------------------------------------------------------------

# 27. Verification before final reboot

Run a complete verification pass.

Check:

``` text
[ ] target disk GUID matches
[ ] D: partition still exists
[ ] D: partition GPT GUID unchanged
[ ] D: filesystem is readable
[ ] Linux root filesystem exists
[ ] root UUID is correct
[ ] /etc/fstab exists
[ ] kernel exists
[ ] initramfs exists
[ ] ESP exists
[ ] EFI/ubuntu exists
[ ] Ubuntu shim exists
[ ] GRUB exists
[ ] UEFI NVRAM entry exists
[ ] Windows Boot Manager entry exists
[ ] BootOrder is valid
[ ] Secure Boot state is compatible
```

Only then reboot.

------------------------------------------------------------------------

# 28. First Linux boot

After reboot:

``` text
UEFI
 |
 +-- ubuntu
      |
      v
   shim
      |
      v
   GRUB
      |
      +-- Ubuntu
      |
      +-- Windows Boot Manager
```

Ubuntu boots normally.

The installation is now independent of:

-   WSL
-   the Windows filesystem
-   the Windows user profile
-   the Windows application

------------------------------------------------------------------------

# 29. Post-install cleanup

After Ubuntu has successfully booted, the temporary installer artifacts
should be removable.

Do not remove anything required by Ubuntu.

The Windows application can later detect:

``` text
installation complete
```

and remove temporary boot entries/payloads.

Do not remove:

``` text
ubuntu
Windows Boot Manager
```

only temporary installer entries.

------------------------------------------------------------------------

# 30. Suggested project structure

Rust example:

``` text
win2linux/
|
+-- Cargo.toml
|
+-- src/
|   |
|   +-- main.rs
|   |
|   +-- ui/
|   |   +-- ...
|   |
|   +-- installer/
|   |   +-- mod.rs
|   |   +-- state.rs
|   |   +-- transaction.rs
|   |
|   +-- system/
|   |   +-- firmware.rs
|   |   +-- secure_boot.rs
|   |   +-- wsl.rs
|   |
|   +-- storage/
|   |   +-- discovery.rs
|   |   +-- volume.rs
|   |   +-- ntfs.rs
|   |   +-- validation.rs
|   |
|   +-- download/
|   |   +-- iso.rs
|   |   +-- checksum.rs
|   |
|   +-- boot/
|   |   +-- windows.rs
|   |   +-- installer_boot.rs
|   |
|   +-- ipc/
|       +-- service.rs
|
+-- service/
|   +-- ...
|
+-- linux-installer/
|   |
|   +-- installer
|   +-- scripts
|   +-- configuration
|   +-- ...
|
+-- wsl/
|   +-- bootstrap.sh
|   +-- helper.sh
|
+-- tests/
    +-- storage/
    +-- state/
    +-- boot/
```

------------------------------------------------------------------------

# 31. Privilege separation

The GUI should not perform all privileged operations itself.

Use:

``` text
Normal GUI process
        |
        | IPC
        v
Windows elevated service
        |
        +-- storage operations
        +-- boot preparation
        +-- protected filesystem operations
        +-- state transitions
```

This provides a much cleaner security boundary.

The GUI should ask the service to perform operations.

The service validates every request independently.

Never trust parameters sent by the GUI.

------------------------------------------------------------------------

# 32. Security requirements

Treat the installer as security-sensitive software.

Requirements:

-   run privileged code only when necessary
-   validate every target disk
-   never accept an arbitrary device path without validation
-   verify downloaded ISO checksums
-   preferably verify distro release signatures using official Ubuntu
    signing mechanisms
-   do not execute arbitrary scripts downloaded from the internet
-   use a fixed, audited installer payload
-   use least privilege where possible
-   log every destructive operation
-   require explicit user confirmation before partition modification
-   never silently disable Secure Boot
-   never silently disable BitLocker
-   never delete Windows partitions
-   never modify unrelated disks

------------------------------------------------------------------------

# 33. Logging

Use structured logs.

Example:

``` text
INFO  discovered physical disk
INFO  disk_guid=...
INFO  target_volume=D:
INFO  original_size=1000000000000
INFO  requested_linux_size=500000000000
INFO  windows_shrink_completed
INFO  shrink_verification_passed
INFO  reboot_environment_prepared
INFO  booting_linux_installer
INFO  target_disk_verified
INFO  linux_partition_created
INFO  ext4_created
INFO  ubuntu_installed
INFO  efi_files_verified
INFO  uefi_entry_created label=ubuntu
INFO  windows_entry_verified
INFO  installation_complete
```

Never log secrets, BitLocker keys, or sensitive user data.

------------------------------------------------------------------------

# 34. Testing strategy

Do not test this first on a developer's primary computer.

Use:

-   Hyper-V virtual machines
-   VMware/VirtualBox where appropriate
-   dedicated physical test machines
-   disposable SSDs

Build a test matrix.

## Test cases

### Basic

``` text
UEFI
GPT
Secure Boot OFF
D: NTFS
```

### Secure Boot

``` text
UEFI
GPT
Secure Boot ON
```

### Two SSDs

``` text
SSD 1 = Windows
SSD 2 = D: + Linux
```

### Same SSD

``` text
SSD 1
C:
D:
Linux
```

### Large D:

``` text
1 TB D:
500 GB Linux
```

### Small free space

Installer must reject unsafe sizes.

### BitLocker

Installer must detect and handle it explicitly.

### Existing ESP

Verify reuse behavior.

### No ESP

Installer must create/use an appropriate ESP according to the supported
layout.

### Interrupted installation

Test power loss/reboot at every state boundary.

### UEFI NVRAM full

Test boot-entry creation failure.

### Firmware that ignores NVRAM changes

Test fallback/recovery behavior.

------------------------------------------------------------------------

# 35. Important limitation: "guaranteed"

The product must never claim:

> "Your data is guaranteed to be safe."

Instead state:

> "The installer uses Windows-supported filesystem operations and only
> uses the newly-created unallocated space for Linux. Back up important
> data before modifying partitions."

No partitioning software can protect against:

-   SSD failure
-   controller failure
-   power loss
-   firmware bugs
-   pre-existing filesystem corruption
-   unexpected BitLocker state
-   user-selected wrong disk
-   hardware failure

------------------------------------------------------------------------

# 36. V1 scope

Keep the first release narrow.

Support only:

``` text
Windows 11
UEFI
GPT
x86-64
Ubuntu
NTFS target volume
one Linux installation
ext4
Secure Boot supported
```

Do NOT initially support:

``` text
Legacy BIOS
ARM Windows
multiple Linux distributions
LVM
Btrfs
ZFS
RAID
dynamic disks
Storage Spaces
complex BitLocker layouts
disk spanning
encrypted Linux root
custom Secure Boot keys
```

These can be added later.

------------------------------------------------------------------------

# 37. Recommended end-to-end implementation order

## Phase 1 --- Safe read-only discovery

Implement:

``` text
system information
UEFI detection
Secure Boot detection
disk enumeration
partition enumeration
volume enumeration
GPT GUID extraction
NTFS detection
free-space detection
```

No destructive operations.

Output a detailed diagnostic report.

------------------------------------------------------------------------

## Phase 2 --- WSL

Implement:

``` text
WSL detection
WSL installation
Debian installation
WSL helper command execution
helper environment versioning
```

Verify that the application can execute:

``` text
wsl.exe -d Debian -- uname -a
```

and receive a valid response.

------------------------------------------------------------------------

## Phase 3 --- ISO handling

Implement:

``` text
Ubuntu release metadata
ISO download
checksum verification
signature verification
local cache
```

Never trust an ISO merely because HTTPS succeeded.

------------------------------------------------------------------------

## Phase 4 --- Disk shrink

Implement the Windows storage operation.

Start with a test volume on a VM.

Required behavior:

``` text
discover
validate
calculate
request shrink
verify
persist checkpoint
```

------------------------------------------------------------------------

## Phase 5 --- Linux installer environment

Build/prepare the Linux environment.

Verify:

``` text
UEFI boot
Linux kernel
installer payload
target disk discovery
```

Do not install anything yet.

------------------------------------------------------------------------

## Phase 6 --- Partition creation

After reboot:

``` text
identify disk
verify GPT GUID
verify D: GUID
verify unallocated space
create Linux partition
```

Abort if anything differs unexpectedly.

------------------------------------------------------------------------

## Phase 7 --- Ubuntu installation

Automate Ubuntu installation into the new partition.

Verify:

``` text
root filesystem
kernel
initramfs
fstab
users
network
```

------------------------------------------------------------------------

## Phase 8 --- EFI

Install:

``` text
shim
GRUB
EFI configuration
```

Create:

``` text
ubuntu
```

UEFI entry.

Verify Windows remains present.

------------------------------------------------------------------------

## Phase 9 --- Reboot validation

Boot Ubuntu.

Verify the first boot.

Then verify Windows can still boot.

------------------------------------------------------------------------

# 38. Final desired user experience

The user should experience:

``` text
Launch Win2Linux
       |
       v
"Install Ubuntu"
       |
       v
Select D:
       |
       v
"Reserve 500 GB"
       |
       v
Safety confirmation
       |
       v
Download Ubuntu
       |
       v
Prepare installation
       |
       v
Restart
       |
       v
Automatic Linux installation
       |
       v
Restart
       |
       v
Ubuntu
```

No USB drive is required.

The final result is:

``` text
Physical SSD
+----------------------------------------------+
| D: NTFS                                      |
| existing Windows files                       |
+----------------------------------------------+
| EFI System Partition                         |
+----------------------------------------------+
| Ubuntu ext4                                  |
|                                              |
| /boot                                        |
| /etc                                         |
| /usr                                         |
| /home                                        |
| /var                                         |
+----------------------------------------------+
```

with UEFI:

``` text
Boot0000* Windows Boot Manager
Boot0001* ubuntu
```

and:

``` text
BootOrder: ubuntu, Windows Boot Manager
```

------------------------------------------------------------------------

# 39. Non-negotiable safety rules for the AI implementation agent

The implementation agent MUST:

1.  Never format the original Windows NTFS volume.
2.  Never delete a partition automatically.
3.  Never assume disk numbering is stable.
4.  Never identify the target disk only by `/dev/nvmeX`.
5.  Never modify a disk that does not match the transaction's recorded
    GPT identity.
6.  Never continue if the disk topology differs unexpectedly.
7.  Never silently disable BitLocker.
8.  Never silently disable Secure Boot.
9.  Never overwrite the Windows EFI directory.
10. Never delete the Windows UEFI boot entry.
11. Never implement NTFS shrinking from scratch.
12. Never implement GPT writing from scratch unless absolutely
    necessary.
13. Never implement an unsigned production EFI bootloader.
14. Never claim data preservation is guaranteed.
15. Require explicit confirmation immediately before destructive storage
    operations.
16. Persist transaction state before and after destructive operations.
17. Verify every destructive operation before proceeding.
18. Make recovery possible after an interrupted reboot.
19. Treat all downloaded artifacts as untrusted until verified.
20. Refuse to operate on unsupported storage configurations rather than
    guessing.

------------------------------------------------------------------------

# 40. Key architectural conclusion

The correct mental model is NOT:

``` text
Windows
  -> WSL
      -> turn WSL into Ubuntu
          -> somehow tell BIOS
```

The correct model is:

``` text
Windows
  |
  +--> WSL helper environment
  |       |
  |       +--> prepare/verify payloads
  |
  +--> Windows storage APIs
  |       |
  |       +--> safely shrink D:
  |
  +--> temporary Linux installer boot
          |
          +--> identify target disk
          +--> create Linux partition
          +--> install Ubuntu
          +--> install signed Ubuntu EFI boot chain
          +--> create UEFI NVRAM "ubuntu" entry
          +--> verify Windows entry
          |
          v
        reboot
          |
          v
       Ubuntu
```

WSL is therefore a **helper**, while the post-reboot Linux environment
is the **actual installer**.

This separation is the key design decision that keeps the project
technically sane and makes the final system a genuine Ubuntu
installation rather than a modified WSL instance.

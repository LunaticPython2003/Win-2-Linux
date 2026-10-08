# Ubuntu kernel selection

Win2Linux exposes a Lenovo Legion kernel option as `linux-7.3-legion`.

When that option is selected for Ubuntu, the generated autoinstall configuration:

- Checks Secure Boot before storage changes. Ubuntu mainline kernel packages are unsigned, so setup stops before partitioning when Secure Boot is enabled.
- Downloads the latest available Ubuntu mainline 7.3 build during installation. It chooses the stable `v7.3` build when published; until then, it uses the highest published `v7.3-rcN` build.
- Downloads the generic amd64 image, modules, and headers over HTTPS and verifies each package against the build's published SHA-256 checksums.
- Applies the Lenovo Legion CS35L41 speaker routing options, regenerates initramfs and GRUB, and leaves Ubuntu's original kernel installed as a fallback.
- Writes the result to `/var/log/win2linux-kernel-selection.log`. If the download or install fails, Ubuntu setup continues with its default kernel.

This choice requires an internet connection during installation and Secure Boot disabled in firmware. Linux mainline builds are for testing and debugging; they do not receive Ubuntu's normal release support. As of 2026-10-09, the Ubuntu mainline archive lists a 7.3 release candidate, not a stable `v7.3` build. The selector therefore installs the latest published 7.3 release candidate until a stable build appears.

The ordinary Ubuntu default kernel option does not run the mainline download or the Secure Boot preflight.

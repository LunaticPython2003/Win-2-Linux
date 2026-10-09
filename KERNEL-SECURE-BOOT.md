# Secure Boot with Ubuntu kernel 7.3

The `linux-7.3-legion` selection installs Ubuntu's signed 7.3 generic kernel from the Ubuntu 26.10 (`stonking`) archive. It does not use the unsigned upstream mainline build and does not generate or enroll a local Machine Owner Key (MOK).

The installer adds the official Ubuntu archive and its updates/security pockets, pins Stonking packages below the installed Ubuntu release by default, and raises priority only for the kernel packages needed by the 7.3 metapackage. It installs `linux-generic/stonking`, confirms that a 7.3 image exists and has a Secure Boot signature, applies the Legion CS35L41 audio options, rebuilds initramfs, and updates GRUB. The existing Ubuntu kernel remains installed as a fallback.

The Windows boot manager remains a separate firmware entry. Ubuntu's GRUB menu is regenerated with OS probing enabled so it can include Windows. The installer requires network access to retrieve the kernel packages.

## Compatibility note

Ubuntu 26.10 is a development release as of October 2026. Installing only its kernel stack on Ubuntu 26.04 LTS avoids upgrading the rest of the operating system, but it is still a cross-release kernel combination. Ubuntu does not certify that combination as an LTS configuration. The signed kernel can boot with Secure Boot enabled because it is signed by Canonical; no Microsoft private signing key or user MOK enrollment is involved.

If the Stonking package installation or signature check fails, the installer records the failure in `/var/log/win2linux-kernel-selection.log`; the Ubuntu 26.04 kernel remains available.

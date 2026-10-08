using System.IO;
using System.Text;

namespace Win2Linux.Core.Boot;

/// <summary>
/// Generates a zero-dependency lightweight CPIO (newc format) initrd overlay archive (win2linux.cpio).
/// When loaded alongside initrd in GRUB (initrd /win2linux/boot/initrd /win2linux/boot/win2linux.cpio),
/// the Linux kernel unpacks this archive into the initial ramdisk.
///
/// It injects a dracut pre-pivot hook (/usr/lib/dracut/hooks/pre-pivot/99-win2linux.sh) which runs
/// after the live root filesystem is mounted at $NEWROOT (/sysroot), bridging the Windows-staged
/// Kickstart file into the Fedora Anaconda Live installer and enabling seamless automated dual-boot.
/// </summary>
public static class InitrdOverlayService
{
    public const string HookScriptContent = """
        #!/bin/sh
        # Win2Linux Dracut Pre-Pivot Hook for Fedora Live
        # Bridges Windows-staged Kickstart into Fedora Anaconda Live installer

        NEWROOT="${NEWROOT:-/sysroot}"

        # Locate the EFI partition labeled LINUXEFI or containing win2linux kickstart
        ESP_MOUNT="/run/win2linux_esp"
        mkdir -p "$ESP_MOUNT"

        ESP_DEV=""
        if [ -e "/dev/disk/by-label/LINUXEFI" ]; then
            ESP_DEV="/dev/disk/by-label/LINUXEFI"
        else
            ESP_DEV=$(blkid -L LINUXEFI 2>/dev/null)
        fi

        if [ -n "$ESP_DEV" ]; then
            mount -o ro "$ESP_DEV" "$ESP_MOUNT" 2>/dev/null
        fi

        # Fallback: scan vfat block devices for /win2linux/unattended/kickstart.ks
        if [ ! -f "$ESP_MOUNT/win2linux/unattended/kickstart.ks" ]; then
            umount "$ESP_MOUNT" 2>/dev/null
            for d in $(blkid -t TYPE=vfat -o device 2>/dev/null); do
                mount -o ro "$d" "$ESP_MOUNT" 2>/dev/null
                if [ -f "$ESP_MOUNT/win2linux/unattended/kickstart.ks" ]; then
                    ESP_DEV="$d"
                    break
                fi
                umount "$ESP_MOUNT" 2>/dev/null
            done
        fi

        if [ -f "$ESP_MOUNT/win2linux/unattended/kickstart.ks" ]; then
            # Stage kickstart in the live root filesystem
            mkdir -p "$NEWROOT/etc/win2linux"
            cp "$ESP_MOUNT/win2linux/unattended/kickstart.ks" "$NEWROOT/etc/win2linux/kickstart.ks"
            chmod 644 "$NEWROOT/etc/win2linux/kickstart.ks"

            # Wrap /usr/bin/liveinst so any invocation (e.g. clicking 'Install to Hard Drive') passes --kickstart
            if [ -f "$NEWROOT/usr/bin/liveinst" ]; then
                if [ ! -f "$NEWROOT/usr/bin/liveinst.stock" ]; then
                    mv "$NEWROOT/usr/bin/liveinst" "$NEWROOT/usr/bin/liveinst.stock"
                    cat << 'EOF' > "$NEWROOT/usr/bin/liveinst"
        #!/bin/bash
        if [ -f /etc/win2linux/kickstart.ks ]; then
            exec anaconda --liveinst --kickstart /etc/win2linux/kickstart.ks "$@"
        else
            exec /usr/bin/liveinst.stock "$@"
        fi
        EOF
                    chmod 755 "$NEWROOT/usr/bin/liveinst"
                fi
            fi

            # Install autostart desktop entry so the installer opens pre-populated on login
            mkdir -p "$NEWROOT/etc/xdg/autostart"
            cat << 'EOF' > "$NEWROOT/etc/xdg/autostart/win2linux-installer.desktop"
        [Desktop Entry]
        Type=Application
        Name=Install Fedora Dual-Boot (Pre-configured)
        Comment=Win2Linux Automated Dual-Boot Installer
        Exec=/usr/bin/liveinst
        Icon=anaconda
        Terminal=false
        StartupNotify=true
        Categories=System;
        X-KDE-autostart-after=panel
        EOF
            chmod 644 "$NEWROOT/etc/xdg/autostart/win2linux-installer.desktop"

            # Also place desktop shortcut on liveuser's desktop
            for d in "$NEWROOT/home/liveuser/Desktop" "$NEWROOT/root/Desktop"; do
                mkdir -p "$d"
                cp "$NEWROOT/etc/xdg/autostart/win2linux-installer.desktop" "$d/win2linux-installer.desktop" 2>/dev/null
                chmod 755 "$d/win2linux-installer.desktop" 2>/dev/null
            done
        fi

        umount "$ESP_MOUNT" 2>/dev/null
        exit 0
        """;

    /// <summary>
    /// Builds the win2linux.cpio overlay archive containing the dracut pre-pivot hook.
    /// </summary>
    public static async Task<byte[]> BuildOverlayArchiveBytesAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            using var ms = new MemoryStream();

            // Normalized Unix LF line endings
            var normalizedHook = HookScriptContent.Replace("\r\n", "\n");
            var hookBytes = Encoding.UTF8.GetBytes(normalizedHook);

            // Add directory hierarchy
            WriteCpioEntry(ms, "usr", Array.Empty<byte>(), isDirectory: true);
            WriteCpioEntry(ms, "usr/lib", Array.Empty<byte>(), isDirectory: true);
            WriteCpioEntry(ms, "usr/lib/dracut", Array.Empty<byte>(), isDirectory: true);
            WriteCpioEntry(ms, "usr/lib/dracut/hooks", Array.Empty<byte>(), isDirectory: true);
            WriteCpioEntry(ms, "usr/lib/dracut/hooks/pre-pivot", Array.Empty<byte>(), isDirectory: true);

            // Add executable hook script
            WriteCpioEntry(ms, "usr/lib/dracut/hooks/pre-pivot/99-win2linux.sh", hookBytes, isDirectory: false);

            // Add TRAILER!!! marker
            WriteCpioTrailer(ms);

            // Pad archive to 512-byte boundary
            var remainder = (int)(ms.Length % 512);
            if (remainder != 0)
            {
                var pad = new byte[512 - remainder];
                ms.Write(pad, 0, pad.Length);
            }

            return ms.ToArray();
        }, ct);
    }

    /// <summary>
    /// Writes win2linux.cpio to the specified destination path.
    /// </summary>
    public static async Task<string> WriteOverlayArchiveAsync(string destinationPath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var bytes = await BuildOverlayArchiveBytesAsync(ct);
        await File.WriteAllBytesAsync(destinationPath, bytes, ct);
        return destinationPath;
    }

    private static void WriteCpioEntry(Stream stream, string path, byte[] data, bool isDirectory)
    {
        // Mode: 040755 for directory (16877 dec, 0x41ed), 0100755 for executable file (33261 dec, 0x81ed)
        uint mode = isDirectory ? 0x000041edu : 0x000081edu;
        uint fileSize = isDirectory ? 0u : (uint)data.Length;

        var nameBytes = Encoding.UTF8.GetBytes(path + "\0");
        uint nameSize = (uint)nameBytes.Length;

        // newc CPIO ASCII header: 110 bytes
        // Format: magic(6), ino(8), mode(8), uid(8), gid(8), nlink(8), mtime(8),
        //         filesize(8), devmajor(8), devminor(8), rdevmajor(8), rdevminor(8),
        //         namesize(8), check(8)
        var header = string.Format(
            "070701{0:x8}{1:x8}{2:x8}{3:x8}{4:x8}{5:x8}{6:x8}{7:x8}{8:x8}{9:x8}{10:x8}{11:x8}{12:x8}",
            1,          // ino
            mode,       // mode
            0,          // uid
            0,          // gid
            1,          // nlink
            0,          // mtime
            fileSize,   // filesize
            0,          // devmajor
            0,          // devminor
            0,          // rdevmajor
            0,          // rdevminor
            nameSize,   // namesize
            0           // check
        );

        var headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes, 0, headerBytes.Length);

        // Path (including null terminator)
        stream.Write(nameBytes, 0, nameBytes.Length);

        // Header + Name must be padded to 4-byte boundary
        int namePadding = (int)((110 + nameSize) % 4);
        if (namePadding != 0)
        {
            for (int i = 0; i < (4 - namePadding); i++)
                stream.WriteByte(0);
        }

        // File payload
        if (fileSize > 0)
        {
            stream.Write(data, 0, data.Length);

            // Payload must be padded to 4-byte boundary
            int dataPadding = (int)(fileSize % 4);
            if (dataPadding != 0)
            {
                for (int i = 0; i < (4 - dataPadding); i++)
                    stream.WriteByte(0);
            }
        }
    }

    private static void WriteCpioTrailer(Stream stream)
    {
        WriteCpioEntry(stream, "TRAILER!!!", Array.Empty<byte>(), isDirectory: false);
    }
}
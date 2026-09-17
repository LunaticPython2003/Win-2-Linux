using Win2Linux.Core.Download;
using Xunit;

namespace Win2Linux.Tests;

public class ChecksumTests
{
    [Fact]
    public void ParsesFedoraBsdChecksumFormatWithPgpWrapper()
    {
        var fedoraChecksumContent = """
            -----BEGIN PGP SIGNED MESSAGE-----
            Hash: SHA256

            # Fedora-Everything-netinst-x86_64-44-1.7.iso: 1161359360 bytes
            SHA256 (Fedora-Everything-netinst-x86_64-44-1.7.iso) = a2dd3caf3224b8f3a640d9e31b1016d2a4e98a6d7cb435a1e2030235976d6da2
            -----BEGIN PGP SIGNATURE-----

            iQIzBAEBCAAdFiEERmzy2LYLwwV6qUU+0GIkYumdatEFAmcg/uIACgkQ0GIkYumd
            ...
            -----END PGP SIGNATURE-----
            """;

        var hash = IsoDownloadService.ParseExpectedHash(
            fedoraChecksumContent,
            "Fedora-Everything-netinst-x86_64-44-1.7.iso");

        Assert.Equal("a2dd3caf3224b8f3a640d9e31b1016d2a4e98a6d7cb435a1e2030235976d6da2", hash);
    }

    [Fact]
    public void ParsesFedoraKdeBsdChecksumFormat()
    {
        var fedoraKdeChecksumContent = """
            -----BEGIN PGP SIGNED MESSAGE-----
            Hash: SHA256

            # Fedora-KDE-Desktop-Live-44-1.7.x86_64.iso: 3368683520 bytes
            SHA256 (Fedora-KDE-Desktop-Live-44-1.7.x86_64.iso) = c8295961d4c41adbf785a31a17c21a971d3b7415fda72dcad0c11c49577bf03a
            -----BEGIN PGP SIGNATURE-----
            ...
            -----END PGP SIGNATURE-----
            """;

        // Exact match
        var hash = IsoDownloadService.ParseExpectedHash(
            fedoraKdeChecksumContent,
            "Fedora-KDE-Desktop-Live-44-1.7.x86_64.iso");

        Assert.Equal("c8295961d4c41adbf785a31a17c21a971d3b7415fda72dcad0c11c49577bf03a", hash);

        // Fallback match on different naming like Fedora-KDE-Live-x86_64-44-1.7.iso
        var fallbackHash = IsoDownloadService.ParseExpectedHash(
            fedoraKdeChecksumContent,
            "Fedora-KDE-Live-x86_64-44-1.7.iso");

        Assert.Equal("c8295961d4c41adbf785a31a17c21a971d3b7415fda72dcad0c11c49577bf03a", fallbackHash);
    }

    [Fact]
    public void ParsesUbuntuCoreutilsAsteriskFormat()
    {
        var ubuntuChecksumContent = """
            89849206d0938f322409f6e622b7d519b5bfb6c927f8a329d479bc2ab5ee62a2 *ubuntu-26.04.1-live-server-amd64.iso
            """;

        var hash = IsoDownloadService.ParseExpectedHash(
            ubuntuChecksumContent,
            "ubuntu-26.04.1-live-server-amd64.iso");

        Assert.Equal("89849206d0938f322409f6e622b7d519b5bfb6c927f8a329d479bc2ab5ee62a2", hash);
    }

    [Fact]
    public void ParsesDebianCoreutilsSpacesFormat()
    {
        var debianChecksumContent = """
            78b408dc3257f86ebffbbfcae58d4a6faefbfb6efd42dd3caf3224b8f3a640d9  debian-12.8.0-amd64-netinst.iso
            """;

        var hash = IsoDownloadService.ParseExpectedHash(
            debianChecksumContent,
            "debian-12.8.0-amd64-netinst.iso");

        Assert.Equal("78b408dc3257f86ebffbbfcae58d4a6faefbfb6efd42dd3caf3224b8f3a640d9", hash);
    }

    [Fact]
    public void ParsesSingleHashFormat()
    {
        var singleHashContent = "a2dd3caf3224b8f3a640d9e31b1016d2a4e98a6d7cb435a1e2030235976d6da2\n";

        var hash = IsoDownloadService.ParseExpectedHash(
            singleHashContent,
            "custom-linux.iso");

        Assert.Equal("a2dd3caf3224b8f3a640d9e31b1016d2a4e98a6d7cb435a1e2030235976d6da2", hash);
    }
}

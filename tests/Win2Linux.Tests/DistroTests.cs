using Win2Linux.Core.Distros;
using Xunit;

namespace Win2Linux.Tests;

public class DistroTests
{
    [Fact]
    public void ExactlyFourSupportedDistrosConfigured()
    {
        var distros = DistroRegistry.SupportedDistributions;
        Assert.Equal(4, distros.Count);

        var ids = distros.Select(d => d.Id).ToList();
        Assert.Contains("ubuntu", ids);
        Assert.Contains("fedora", ids);
        Assert.Contains("linuxmint", ids);
        Assert.Contains("zorin", ids);

        // Verify deprecated/unrequested distros are strictly excluded
        Assert.DoesNotContain("debian", ids);
        Assert.DoesNotContain("opensuse", ids);
        Assert.DoesNotContain("fedora-netinstall", ids);
    }

    [Fact]
    public void AllSupportedDistrosHaveSecureBootCertification()
    {
        var distros = DistroRegistry.SupportedDistributions;
        Assert.NotEmpty(distros);

        foreach (var d in distros)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Id));
            Assert.False(string.IsNullOrWhiteSpace(d.DisplayName));
            Assert.Contains("Microsoft UEFI CA", d.SecureBootStatus);
            Assert.False(string.IsNullOrWhiteSpace(d.EfiVendorDir));
            Assert.False(string.IsNullOrWhiteSpace(d.EfiBinary));
            Assert.StartsWith("https://", d.IsoDownloadUrl);
            Assert.StartsWith("https://", d.ChecksumUrl);
        }
    }

    [Fact]
    public void FedoraHasKdePlasmaDesktopEnvironmentAsDefault()
    {
        var fedora = DistroRegistry.SupportedDistributions.First(d => d.Id == "fedora");
        Assert.NotNull(fedora.DesktopEnvironments);
        Assert.Equal(2, fedora.DesktopEnvironments.Count);

        var deIds = fedora.DesktopEnvironments.Select(de => de.Id).ToList();
        Assert.Contains("kde", deIds);
        Assert.Contains("gnome", deIds);

        // User preference: KDE Plasma is default
        Assert.Equal("kde", fedora.SelectedDesktopEnvironment);

        var kdeOption = fedora.DesktopEnvironments.First(de => de.Id == "kde");
        Assert.Equal("@^kde-desktop-environment", kdeOption.PackageGroup);
        Assert.Contains("KDE", fedora.IsoDownloadUrl);

        // Switching to GNOME works seamlessly
        var gnomeFedora = fedora.WithDesktopEnvironment("gnome");
        Assert.Equal("gnome", gnomeFedora.SelectedDesktopEnvironment);
        Assert.Contains("Workstation", gnomeFedora.IsoDownloadUrl);
    }
}

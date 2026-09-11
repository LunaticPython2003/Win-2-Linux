using Win2Linux.Core.Distros;

namespace Win2Linux.Tests;

public class DistroTests
{
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
    public void ContainsUbuntuAndFedora()
    {
        var distros = DistroRegistry.SupportedDistributions;
        Assert.Contains(distros, d => d.Id == "ubuntu" && d.EfiVendorDir == "ubuntu");
        Assert.Contains(distros, d => d.Id == "fedora" && d.EfiVendorDir == "fedora");
        Assert.Contains(distros, d => d.Id == "debian" && d.EfiVendorDir == "debian");
    }
}

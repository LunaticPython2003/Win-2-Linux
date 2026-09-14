using Win2Linux.Core.Distros;
using Win2Linux.Core.Download;
using Xunit;

namespace Win2Linux.Tests;

public class IsoInferenceTests
{
    [Fact]
    public void GetRepoIsoDirectoryReturnsValidPathEndingInRepoIso()
    {
        var dir = IsoDownloadService.GetRepoIsoDirectory();
        Assert.NotNull(dir);
        Assert.EndsWith(Path.Combine("repo", "iso"), dir, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void GetDefaultIsoDownloadPathPointsToRepoIso()
    {
        var distro = DistroRegistry.SupportedDistributions.First(d => d.Id == "ubuntu");
        var path = IsoDownloadService.GetDefaultIsoDownloadPath(distro);

        Assert.Contains(Path.Combine("repo", "iso"), path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith($"{distro.Id}-{distro.DefaultVersion}.iso", path, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ubuntu-26.04.1-live-server-amd64.iso", "ubuntu")]
    [InlineData("Fedora-Workstation-Live-44-1.7.x86_64.iso", "fedora")]
    [InlineData("Fedora-Everything-netinst-x86_64-44-1.7.iso", "fedora-netinstall")]
    [InlineData("debian-12.8.0-amd64-netinst.iso", "debian")]
    [InlineData("openSUSE-Tumbleweed-NET-x86_64-Current.iso", "opensuse")]
    public void InferDistroFromIsoMatchesCorrectDistro(string fileName, string expectedDistroId)
    {
        var profile = IsoDownloadService.InferDistroFromIso(fileName);
        Assert.NotNull(profile);
        Assert.Equal(expectedDistroId, profile.Id);
    }

    [Fact]
    public void InferIsoPathDetectsCandidateFileInDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "win2linux_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var testDistro = DistroRegistry.SupportedDistributions.First(d => d.Id == "ubuntu");
            var fakeIso = Path.Combine(tempDir, "ubuntu-26.04.1-live-server-amd64.iso");
            File.WriteAllText(fakeIso, "fake iso content");

            var inferred = IsoDownloadService.InferIsoPath(testDistro, tempDir);
            Assert.NotNull(inferred);
            Assert.Equal(fakeIso, inferred);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}

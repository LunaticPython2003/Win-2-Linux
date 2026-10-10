using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Win2Linux.Core.Distros;
using Win2Linux.Core.Download;
using Win2Linux.Core.Orchestrator;

namespace Win2Linux.Tests;

public sealed class IsoDownloadStorageTests : IDisposable
{
    private const long IsoSize = 3728L * 1024 * 1024;
    private readonly string _work = Path.Combine(Path.GetTempPath(), "win2linux-storage-" + Guid.NewGuid().ToString("N"));
    private readonly DistroProfile _distro = DistroRegistry.SupportedDistributions.First(d => d.Id == "zorin") with
    {
        IsoDownloadUrl = "https://test.invalid/zorin.iso",
        ChecksumUrl = "https://test.invalid/checksum",
        IsoMirrorUrls = ["https://test.invalid/mirror.iso", "https://test.invalid/zorin.iso"]
    };
    private string Cache => Path.Combine(_work, "cache");
    private string Repo => Path.Combine(_work, "repo");
    private string CacheIso => Path.Combine(Cache, $"{_distro.Id}-{_distro.DefaultVersion}.iso");
    private static readonly IProgress<ProgressUpdate> Progress = new InlineProgress();

    [Theory]
    [InlineData("D:")]
    [InlineData("D:\\")]
    public void CacheUsesSelectedVolume(string volume)
        => Assert.Equal(@"D:\win2linux\iso", IsoDownloadService.GetIsoCacheDirectory(volume));

    [Fact]
    public void EmptyTargetVolumeIsRejected()
        => Assert.Throws<ArgumentException>(() => IsoDownloadService.GetIsoCacheDirectory(""));

    [Fact]
    public void CapacityIncludesReserveAndHandlesUnknownLength()
    {
        var reserve = IsoDownloadService.StorageReserveBytes;
        IsoDownloadService.AssertAvailableSpace(CacheIso, IsoSize, IsoSize + reserve);
        Assert.Throws<IsoStorageException>(() => IsoDownloadService.AssertAvailableSpace(CacheIso, IsoSize, IsoSize + reserve - 1));
        IsoDownloadService.AssertAvailableSpace(CacheIso, null, reserve);
        Assert.Throws<IsoStorageException>(() => IsoDownloadService.AssertAvailableSpace(CacheIso, null, reserve - 1));
        Assert.Throws<IsoStorageException>(() => IsoDownloadService.AssertAvailableSpace(CacheIso, long.MaxValue, long.MaxValue));
    }

    [Fact]
    public async Task InsufficientSpaceStopsBeforeWritingOrTryingAnotherMirror()
    {
        using var handler = new DownloadHandler(new string('0', 64), () => IsoResponse(IsoSize));
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<IsoStorageException>(() => Download(http, _ => IsoSize - 1));
        Assert.Contains(CacheIso, ex.Message);
        Assert.Equal(1, handler.IsoRequests);
        Assert.False(File.Exists(CacheIso));
    }

    [Fact]
    public async Task InvalidRepoIsoDoesNotRedirectDownloadToRepo()
    {
        Directory.CreateDirectory(Repo);
        var repoIso = Path.Combine(Repo, Path.GetFileName(CacheIso));
        await File.WriteAllTextAsync(repoIso, "incomplete download");
        using var handler = new DownloadHandler(new string('0', 64), () => IsoResponse(IsoSize));
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<IsoStorageException>(() => Download(http, _ => 0));
        Assert.Contains(CacheIso, ex.Message);
        Assert.Equal("incomplete download", await File.ReadAllTextAsync(repoIso));
        Assert.Equal(1, handler.IsoRequests);
    }

    [Fact]
    public async Task ValidRepoIsoIsReusedEvenWhenTargetCacheIsInvalid()
    {
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Repo);
        await File.WriteAllTextAsync(CacheIso, "partial cache");
        var repoIso = Path.Combine(Repo, Path.GetFileName(CacheIso));
        var bytes = Encoding.UTF8.GetBytes("verified cache fixture");
        await File.WriteAllBytesAsync(repoIso, bytes);
        using var handler = new DownloadHandler(Convert.ToHexString(SHA256.HashData(bytes)), () => throw new InvalidOperationException("ISO transfer was not expected"));
        using var http = new HttpClient(handler);
        var result = await Download(http, _ => throw new InvalidOperationException("No write capacity needed for cache hit"));
        Assert.Equal(repoIso, result);
        Assert.Equal(0, handler.IsoRequests);
        Assert.Equal("partial cache", await File.ReadAllTextAsync(CacheIso));
    }

    [Fact]
    public async Task DestinationWriteFailureDoesNotTryAnotherMirror()
    {
        // A directory at the file path makes FileStream creation fail, even with ample free space.
        Directory.CreateDirectory(CacheIso);
        using var handler = new DownloadHandler(new string('0', 64), () => IsoResponse(IsoSize));
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<IsoStorageException>(() => Download(http, _ => long.MaxValue));
        Assert.Contains(CacheIso, ex.Message);
        Assert.Equal(1, handler.IsoRequests);
        Assert.True(Directory.Exists(CacheIso));
    }

    [Fact]
    public async Task FreeSpaceProbeFailureDoesNotTryAnotherMirror()
    {
        using var handler = new DownloadHandler(new string('0', 64), () => IsoResponse(IsoSize));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<IsoStorageException>(() => Download(http, _ => throw new IOException("Volume unavailable")));
        Assert.Equal(1, handler.IsoRequests);
    }

    [Fact]
    public async Task NetworkReadFailureStillTriesOtherMirrorsAndDeletesPartialFile()
    {
        using var handler = new DownloadHandler(new string('0', 64), () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingNetworkStream()) };
            response.Content.Headers.ContentLength = IsoSize;
            return response;
        });
        using var http = new HttpClient(handler);
        var ex = await Assert.ThrowsAsync<IsoDownloadException>(() => Download(http, _ => long.MaxValue));
        Assert.Contains("All 2 mirror(s) failed", ex.Message);
        Assert.Equal(2, handler.IsoRequests);
        Assert.False(File.Exists(CacheIso));
    }

    private Task<string> Download(HttpClient http, Func<string, long> freeSpace)
        => IsoDownloadService.DownloadIsoAsync(_distro, Cache, Progress, CancellationToken.None, http, Repo, freeSpace);

    private static HttpResponseMessage IsoResponse(long size)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentLength = size;
        return response;
    }

    public void Dispose()
    {
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    private sealed class InlineProgress : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate value) { }
    }

    private sealed class DownloadHandler(string hash, Func<HttpResponseMessage> isoResponse) : HttpMessageHandler
    {
        public int IsoRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/checksum")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{hash}  zorin.iso") });
            IsoRequests++;
            return Task.FromResult(isoResponse());
        }
    }

    private sealed class FailingNetworkStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Connection interrupted");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("Connection interrupted"));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

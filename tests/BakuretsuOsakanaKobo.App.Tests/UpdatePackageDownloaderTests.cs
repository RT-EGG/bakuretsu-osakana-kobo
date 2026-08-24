using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class UpdatePackageDownloaderTests
{
    [Fact]
    public async Task DownloadAsync_WithValidPackage_PreparesVerifiedArchive()
    {
        using var directory = new TemporaryDirectory();
        var packageBytes = CreatePackage();
        using var handler = new PackageHandler(packageBytes);
        using var httpClient = new HttpClient(handler);
        var downloader = new UpdatePackageDownloader(httpClient, new Capacity(long.MaxValue));

        var result = await downloader.DownloadAsync(Asset(packageBytes), directory.Path);

        Assert.Equal(UpdatePackageDownloadStatus.Prepared, result.Status);
        Assert.NotNull(result.Package);
        Assert.True(File.Exists(result.Package.ArchivePath));
        Assert.Equal(packageBytes.Length, new FileInfo(result.Package.ArchivePath).Length);
        Assert.Contains("BakuretsuOsakanaKobo.exe", result.Package.Files);
        Assert.DoesNotContain(result.Package.Files, path => path.StartsWith("data/", StringComparison.OrdinalIgnoreCase));
        UpdatePackageDownloader.DeletePreparedPackage(result.Package);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadAsync_WithAssetSizeOrDigestMismatch_RejectsAndCleans(bool mismatchSize)
    {
        using var directory = new TemporaryDirectory();
        var packageBytes = CreatePackage();
        using var handler = new PackageHandler(packageBytes, includeContentLength: false);
        using var httpClient = new HttpClient(handler);
        var valid = Asset(packageBytes);
        var asset = mismatchSize
            ? valid with { Size = valid.Size + 1 }
            : valid with { Sha256Digest = new string('A', 64) };

        var result = await new UpdatePackageDownloader(httpClient, new Capacity(long.MaxValue))
            .DownloadAsync(asset, directory.Path);

        Assert.Equal(UpdatePackageDownloadStatus.InvalidPackage, result.Status);
        Assert.Null(result.Package);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task DownloadAsync_WithCorruptZip_RejectsAndCleans()
    {
        using var directory = new TemporaryDirectory();
        var bytes = Encoding.UTF8.GetBytes("not a ZIP");
        using var handler = new PackageHandler(bytes);
        using var httpClient = new HttpClient(handler);

        var result = await new UpdatePackageDownloader(httpClient, new Capacity(long.MaxValue))
            .DownloadAsync(Asset(bytes), directory.Path);

        Assert.Equal(UpdatePackageDownloadStatus.InvalidPackage, result.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("data/settings.json")]
    [InlineData("plugins\\bad.dll")]
    [InlineData("CON")]
    [InlineData("bad-name.")]
    [InlineData("bad?.dll")]
    public async Task DownloadAsync_WithUnsafeOrDataPath_RejectsAndCleans(string unsafePath)
    {
        using var directory = new TemporaryDirectory();
        var bytes = CreatePackage((unsafePath, Encoding.UTF8.GetBytes("bad")));
        using var handler = new PackageHandler(bytes);
        using var httpClient = new HttpClient(handler);

        var result = await new UpdatePackageDownloader(httpClient, new Capacity(long.MaxValue))
            .DownloadAsync(Asset(bytes), directory.Path);

        Assert.Equal(UpdatePackageDownloadStatus.InvalidPackage, result.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task DownloadAsync_WithManifestHashMismatch_RejectsAndCleans()
    {
        using var directory = new TemporaryDirectory();
        var bytes = CreatePackage(manifestHashOverride: new string('0', 64));
        using var handler = new PackageHandler(bytes);
        using var httpClient = new HttpClient(handler);

        var result = await new UpdatePackageDownloader(httpClient, new Capacity(long.MaxValue))
            .DownloadAsync(Asset(bytes), directory.Path);

        Assert.Equal(UpdatePackageDownloadStatus.InvalidPackage, result.Status);
        Assert.Contains("SHA-256", result.TechnicalMessage);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task DownloadAsync_WhenInitialSpaceIsInsufficient_DoesNotRequestOrCreateFiles()
    {
        using var directory = new TemporaryDirectory();
        var bytes = CreatePackage();
        using var handler = new PackageHandler(bytes);
        using var httpClient = new HttpClient(handler);

        var result = await new UpdatePackageDownloader(httpClient, new Capacity(bytes.Length - 1))
            .DownloadAsync(Asset(bytes), directory.Path);

        Assert.Equal(UpdatePackageDownloadStatus.InsufficientSpace, result.Status);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData(536870913L, "https://github.com/RT-EGG/bakuretsu-osakana-kobo/releases/download/v1.2.0/BakuretsuOsakanaKobo-win-x64.zip")]
    [InlineData(100L, "https://github.com/another/repository/releases/download/v1.2.0/BakuretsuOsakanaKobo-win-x64.zip")]
    public async Task DownloadAsync_WithUnapprovedMetadata_DoesNotRequestOrCreateFiles(
        long size,
        string downloadUrl)
    {
        using var directory = new TemporaryDirectory();
        var bytes = CreatePackage();
        using var handler = new PackageHandler(bytes);
        using var httpClient = new HttpClient(handler);
        var asset = Asset(bytes) with { Size = size, DownloadUri = new Uri(downloadUrl) };

        var result = await new UpdatePackageDownloader(httpClient, new Capacity(long.MaxValue))
            .DownloadAsync(asset, directory.Path);

        Assert.Equal(UpdatePackageDownloadStatus.InvalidPackage, result.Status);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task DownloadAsync_WhenStagingSpaceIsInsufficient_RemovesDownloadedArchive()
    {
        using var directory = new TemporaryDirectory();
        var bytes = CreatePackage();
        using var handler = new PackageHandler(bytes);
        using var httpClient = new HttpClient(handler);

        var result = await new UpdatePackageDownloader(
                httpClient,
                new Capacity(long.MaxValue, 0))
            .DownloadAsync(Asset(bytes), directory.Path);

        Assert.Equal(UpdatePackageDownloadStatus.InsufficientSpace, result.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task DownloadAsync_WhenHttpFails_ReturnsUnavailableAndCleans()
    {
        using var directory = new TemporaryDirectory();
        using var handler = new StatusHandler(HttpStatusCode.ServiceUnavailable);
        using var httpClient = new HttpClient(handler);
        var bytes = CreatePackage();

        var result = await new UpdatePackageDownloader(httpClient, new Capacity(long.MaxValue))
            .DownloadAsync(Asset(bytes), directory.Path);

        Assert.Equal(UpdatePackageDownloadStatus.Unavailable, result.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task DownloadAsync_WhenCallerCancelsMidDownload_PropagatesAndCleans()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var bytes = CreatePackage();
        using var handler = new CancelingPackageHandler(bytes, cancellation);
        using var httpClient = new HttpClient(handler);
        var downloader = new UpdatePackageDownloader(httpClient, new Capacity(long.MaxValue));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            downloader.DownloadAsync(Asset(bytes), directory.Path, cancellation.Token));

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task DownloadAsync_WhenHttpClientTimesOut_ReturnsUnavailableAndCleans()
    {
        using var directory = new TemporaryDirectory();
        using var handler = new ThrowingHandler(new TaskCanceledException("timeout"));
        using var httpClient = new HttpClient(handler);
        var bytes = CreatePackage();

        var result = await new UpdatePackageDownloader(httpClient, new Capacity(long.MaxValue))
            .DownloadAsync(Asset(bytes), directory.Path);

        Assert.Equal(UpdatePackageDownloadStatus.Unavailable, result.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    private static UpdateAsset Asset(byte[] bytes) => new(
        GitHubReleaseClient.AssetName,
        bytes.LongLength,
        Convert.ToHexString(SHA256.HashData(bytes)),
        new Uri("https://github.com/RT-EGG/bakuretsu-osakana-kobo/releases/download/v1.2.0/BakuretsuOsakanaKobo-win-x64.zip"));

    private static byte[] CreatePackage(
        (string Path, byte[] Content)? additionalFile = null,
        string? manifestHashOverride = null)
    {
        var files = new List<(string Path, byte[] Content)>
        {
            ("BakuretsuOsakanaKobo.exe", Encoding.UTF8.GetBytes("executable")),
            ("libvlc/win-x64/libvlc.dll", Encoding.UTF8.GetBytes("native library")),
        };
        if (additionalFile is { } additional)
        {
            files.Add(additional);
        }

        var manifestFiles = files.Select((file, index) => new
        {
            path = file.Path,
            bytes = file.Content.LongLength,
            sha256 = index == 0 && manifestHashOverride is not null
                ? manifestHashOverride
                : Convert.ToHexString(SHA256.HashData(file.Content)),
        }).ToArray();
        var totalBytes = files.Sum(file => file.Content.LongLength);
        var manifestJson = JsonSerializer.Serialize(new
        {
            validationOnly = false,
            distributionMode = "FrameworkDependent",
            runtimeIdentifier = "win-x64",
            publishSingleFile = false,
            totalFiles = files.Count,
            totalBytes,
            files = manifestFiles,
        });
        manifestJson = manifestJson.Replace(
            $"\"totalBytes\":{totalBytes}",
            $"\"totalBytes\":{totalBytes}.0",
            StringComparison.Ordinal);
        var manifest = Encoding.UTF8.GetBytes(manifestJson);

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                WriteEntry(archive, file.Path, file.Content);
            }

            WriteEntry(archive, "release-manifest.json", manifest);
        }

        return output.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private sealed class Capacity(params long[] values) : IUpdateStorageCapacity
    {
        private int _index;

        public long GetAvailableFreeSpace(string directoryPath)
        {
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, values.Length - 1);
            return values[index];
        }
    }

    private sealed class PackageHandler(byte[] bytes, bool includeContentLength = true) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var content = new ByteArrayContent(bytes);
            if (!includeContentLength)
            {
                content.Headers.ContentLength = null;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class StatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode) { Content = new ByteArrayContent([]) });
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class CancelingPackageHandler(
        byte[] bytes,
        CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new CancelingReadStream(bytes, cancellation)),
            });
    }

    private sealed class CancelingReadStream(
        byte[] bytes,
        CancellationTokenSource cancellation) : Stream
    {
        private int _position;
        private int _reads;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.LongLength;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) > 1)
            {
                cancellation.Cancel();
                return ValueTask.FromCanceled<int>(cancellation.Token);
            }

            var count = Math.Min(16, Math.Min(buffer.Length, bytes.Length - _position));
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "BakuretsuOsakanaKobo.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

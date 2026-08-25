using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BakuretsuOsakanaKobo.Update;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class UpdateApplicationCoordinatorTests
{
    [Fact]
    public async Task DownloadAndLaunchAsync_WithVerifiedPackage_CopiesHelperAndWritesRequest()
    {
        using var fixture = new CoordinatorFixture();

        var result = await fixture.Coordinator.DownloadAndLaunchAsync(fixture.Release, fixture.Target);

        Assert.Equal(ApplicationUpdateStartStatus.Launched, result.Status);
        Assert.NotNull(fixture.Launcher.RequestPath);
        Assert.True(File.Exists(fixture.Launcher.RequestPath));
        Assert.Equal(fixture.Target, fixture.Launcher.Request!.TargetDirectory);
        Assert.Equal(fixture.Release.Asset.Sha256Digest, fixture.Launcher.Request.ArchiveSha256);
        Assert.Equal(fixture.Launcher.LaunchToken, fixture.Launcher.Request.LaunchToken);
        Assert.True(File.Exists(fixture.Launcher.ExecutablePath));
        Assert.True(File.Exists(Path.Combine(
            Path.GetDirectoryName(fixture.Launcher.ExecutablePath)!,
            "BakuretsuOsakanaKobo.Update.dll")));
    }

    [Fact]
    public async Task DownloadAndLaunchAsync_WhenHelperFileIsMissing_CleansPreparedPackage()
    {
        using var fixture = new CoordinatorFixture();
        File.Delete(Path.Combine(fixture.HelperSource, "BakuretsuOsakanaKobo.Update.dll"));

        var result = await fixture.Coordinator.DownloadAndLaunchAsync(fixture.Release, fixture.Target);

        Assert.Equal(ApplicationUpdateStartStatus.HelperUnavailable, result.Status);
        Assert.Equal(0, fixture.Launcher.CallCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.WorkingRoot));
    }

    [Fact]
    public async Task DownloadAndLaunchAsync_WhenHelperDoesNotBecomeReady_CleansPreparedPackage()
    {
        using var fixture = new CoordinatorFixture(helperReady: false);

        var result = await fixture.Coordinator.DownloadAndLaunchAsync(fixture.Release, fixture.Target);

        Assert.Equal(ApplicationUpdateStartStatus.HelperLaunchFailed, result.Status);
        Assert.Equal(1, fixture.Launcher.CallCount);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.WorkingRoot));
    }

    private sealed class CoordinatorFixture : IDisposable
    {
        private readonly string _root;
        private readonly HttpClient _httpClient;

        internal CoordinatorFixture(bool helperReady = true)
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "BakuretsuOsakanaKobo.Update.Coordinator.Tests",
                Guid.NewGuid().ToString("N"));
            Target = Path.Combine(_root, "target");
            HelperSource = Path.Combine(_root, "updater");
            WorkingRoot = Path.Combine(_root, "working");
            Directory.CreateDirectory(Target);
            Directory.CreateDirectory(HelperSource);
            Directory.CreateDirectory(WorkingRoot);
            foreach (var fileName in new[]
                     {
                         "BakuretsuOsakanaKobo.Updater.exe",
                         "BakuretsuOsakanaKobo.Updater.dll",
                         "BakuretsuOsakanaKobo.Updater.deps.json",
                         "BakuretsuOsakanaKobo.Updater.runtimeconfig.json",
                         "BakuretsuOsakanaKobo.Update.dll",
                     })
            {
                File.WriteAllText(Path.Combine(HelperSource, fileName), $"helper:{fileName}");
            }

            var archive = CreatePackage();
            _httpClient = new HttpClient(new PackageHandler(archive));
            var asset = new UpdateAsset(
                GitHubReleaseClient.AssetName,
                archive.LongLength,
                Convert.ToHexString(SHA256.HashData(archive)),
                new Uri("https://github.com/RT-EGG/bakuretsu-osakana-kobo/releases/download/v1.2.0/BakuretsuOsakanaKobo-win-x64.zip"));
            Assert.True(SemanticVersion.TryParse("1.2.0", out var version));
            Release = new UpdateRelease(
                version!,
                "v1.2.0",
                "test",
                "body",
                new Uri("https://github.com/RT-EGG/bakuretsu-osakana-kobo/releases/tag/v1.2.0"),
                asset);
            Launcher = new FakeLauncher(helperReady);
            Coordinator = new UpdateApplicationCoordinator(
                new UpdatePackageDownloader(_httpClient, new Capacity()),
                HelperSource,
                WorkingRoot,
                Launcher);
        }

        internal string Target { get; }

        internal string HelperSource { get; }

        internal string WorkingRoot { get; }

        internal UpdateRelease Release { get; }

        internal FakeLauncher Launcher { get; }

        internal UpdateApplicationCoordinator Coordinator { get; }

        public void Dispose()
        {
            _httpClient.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static byte[] CreatePackage()
        {
            var executable = Encoding.UTF8.GetBytes("executable");
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                validationOnly = false,
                distributionMode = "FrameworkDependent",
                runtimeIdentifier = "win-x64",
                publishSingleFile = false,
                totalFiles = 1,
                totalBytes = executable.LongLength,
                files = new[]
                {
                    new
                    {
                        path = "BakuretsuOsakanaKobo.exe",
                        bytes = executable.LongLength,
                        sha256 = Convert.ToHexString(SHA256.HashData(executable)),
                    },
                },
            });
            using var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteEntry(zip, "BakuretsuOsakanaKobo.exe", executable);
                WriteEntry(zip, "release-manifest.json", manifest);
            }

            return output.ToArray();
        }

        private static void WriteEntry(ZipArchive archive, string path, byte[] bytes)
        {
            var entry = archive.CreateEntry(path);
            using var stream = entry.Open();
            stream.Write(bytes);
        }
    }

    private sealed class FakeLauncher(bool ready) : IUpdateHelperLauncher
    {
        internal int CallCount { get; private set; }

        internal string? ExecutablePath { get; private set; }

        internal string? RequestPath { get; private set; }

        internal string? LaunchToken { get; private set; }

        internal UpdateHelperRequest? Request { get; private set; }

        public async Task<(bool Ready, string? TechnicalMessage)> LaunchAndWaitForReadyAsync(
            string executablePath,
            string workingDirectory,
            string requestPath,
            string readyPath,
            string launchToken,
            CancellationToken cancellationToken)
        {
            CallCount++;
            ExecutablePath = executablePath;
            RequestPath = requestPath;
            LaunchToken = launchToken;
            Request = JsonSerializer.Deserialize<UpdateHelperRequest>(
                await File.ReadAllBytesAsync(requestPath, cancellationToken),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return (ready, ready ? null : "not ready");
        }
    }

    private sealed class Capacity : IUpdateStorageCapacity
    {
        public long GetAvailableFreeSpace(string directoryPath) => long.MaxValue;
    }

    private sealed class PackageHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes),
            });
    }
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BakuretsuOsakanaKobo.Update;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class UpdateHelperHostTests
{
    [Fact]
    public async Task RunAsync_AfterParentExit_RevalidatesReplacesAndRestarts()
    {
        using var fixture = new HelperFixture();

        var result = await fixture.Host.RunAsync(fixture.RequestPath);

        Assert.Equal(UpdateHelperStatus.Succeeded, result.Status);
        Assert.True(result.RestartAttempted);
        Assert.Equal("new executable", fixture.ReadTarget("BakuretsuOsakanaKobo.exe"));
        Assert.Equal("new library", fixture.ReadTarget("new.dll"));
        Assert.False(File.Exists(Path.Combine(fixture.Target, "old.dll")));
        Assert.Equal("settings", fixture.ReadTarget("data/settings.json"));
        Assert.Equal("user file", fixture.ReadTarget("notes.txt"));
        Assert.Equal(Path.Combine(fixture.Target, "BakuretsuOsakanaKobo.exe"), fixture.Process.StartedExecutable);
        Assert.False(File.Exists(fixture.ArchivePath));
        Assert.False(Directory.Exists(Path.Combine(fixture.Working, "staged")));
        Assert.True(File.Exists(fixture.ResultPath));
    }

    [Fact]
    public async Task RunAsync_WhenParentDoesNotExit_DoesNotChangeOrRestart()
    {
        using var fixture = new HelperFixture(ParentProcessWaitStatus.TimedOut);

        var result = await fixture.Host.RunAsync(fixture.RequestPath);

        Assert.Equal(UpdateHelperStatus.ParentExitTimeout, result.Status);
        Assert.False(result.RestartAttempted);
        Assert.Null(fixture.Process.StartedExecutable);
        Assert.Equal("old executable", fixture.ReadTarget("BakuretsuOsakanaKobo.exe"));
        Assert.Equal("old library", fixture.ReadTarget("old.dll"));
        Assert.False(File.Exists(fixture.ArchivePath));
    }

    [Fact]
    public async Task RunAsync_WhenArchiveChangesAfterRequest_RejectsAndRestartsOldVersion()
    {
        using var fixture = new HelperFixture();
        await File.AppendAllTextAsync(fixture.ArchivePath, "tampered");

        var result = await fixture.Host.RunAsync(fixture.RequestPath);

        Assert.Equal(UpdateHelperStatus.InvalidPackage, result.Status);
        Assert.True(result.RestartAttempted);
        Assert.Equal("old executable", fixture.ReadTarget("BakuretsuOsakanaKobo.exe"));
        Assert.Equal("old library", fixture.ReadTarget("old.dll"));
        Assert.Null(result.RollbackFailures);
    }

    [Fact]
    public async Task RunAsync_WhenReplacementFails_RollsBackAndRestartsOldVersion()
    {
        using var fixture = new HelperFixture(
            faultInjector: new ThrowAfterExecutableInstall());

        var result = await fixture.Host.RunAsync(fixture.RequestPath);

        Assert.Equal(UpdateHelperStatus.FailedRolledBack, result.Status);
        Assert.True(result.RestartAttempted);
        Assert.Equal("old executable", fixture.ReadTarget("BakuretsuOsakanaKobo.exe"));
        Assert.Equal("old library", fixture.ReadTarget("old.dll"));
        Assert.False(File.Exists(Path.Combine(fixture.Target, "new.dll")));
        Assert.Equal("settings", fixture.ReadTarget("data/settings.json"));
    }

    [Fact]
    public async Task RunAsync_WithUnknownRequestProperty_RejectsBeforeWaiting()
    {
        using var fixture = new HelperFixture();
        var json = await File.ReadAllTextAsync(fixture.RequestPath);
        await File.WriteAllTextAsync(
            fixture.RequestPath,
            json.TrimEnd('}', '\r', '\n') + ",\"unexpected\":true}");

        var result = await fixture.Host.RunAsync(fixture.RequestPath);

        Assert.Equal(UpdateHelperStatus.InvalidRequest, result.Status);
        Assert.Equal(0, fixture.Process.WaitCalls);
        Assert.Null(fixture.Process.StartedExecutable);
        Assert.Equal("old executable", fixture.ReadTarget("BakuretsuOsakanaKobo.exe"));
    }

    private sealed class HelperFixture : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        private readonly string _root;

        internal HelperFixture(
            ParentProcessWaitStatus waitStatus = ParentProcessWaitStatus.Exited,
            IUpdateTransactionFaultInjector? faultInjector = null)
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "BakuretsuOsakanaKobo.Update.Helper.Tests",
                Guid.NewGuid().ToString("N"));
            Working = Path.Combine(_root, $"update-{Guid.NewGuid():N}");
            Target = Path.Combine(_root, "target");
            Directory.CreateDirectory(Working);
            Directory.CreateDirectory(Target);

            var oldFiles = new Dictionary<string, string>
            {
                ["BakuretsuOsakanaKobo.exe"] = "old executable",
                ["old.dll"] = "old library",
            };
            WriteRelease(Target, oldFiles);
            Write(Target, "data/settings.json", "settings");
            Write(Target, "notes.txt", "user file");

            var nextFiles = new Dictionary<string, string>
            {
                ["BakuretsuOsakanaKobo.exe"] = "new executable",
                ["new.dll"] = "new library",
            };
            ArchivePath = Path.Combine(Working, UpdateHelperHost.ArchiveFileName);
            CreateReleaseArchive(ArchivePath, nextFiles);
            var archiveBytes = File.ReadAllBytes(ArchivePath);

            RequestPath = Path.Combine(Working, UpdateHelperHost.RequestFileName);
            ResultPath = Path.Combine(Working, UpdateHelperHost.ResultFileName);
            var request = new UpdateHelperRequest(
                1,
                1234,
                DateTime.UtcNow.Ticks,
                Target,
                ArchivePath,
                archiveBytes.LongLength,
                Convert.ToHexString(SHA256.HashData(archiveBytes)));
            File.WriteAllText(RequestPath, JsonSerializer.Serialize(request, JsonOptions));

            Process = new FakeProcessController(waitStatus);
            Host = new UpdateHelperHost(
                Process,
                transaction: new UpdateFileTransaction(faultInjector));
        }

        internal string Working { get; }

        internal string Target { get; }

        internal string ArchivePath { get; }

        internal string RequestPath { get; }

        internal string ResultPath { get; }

        internal FakeProcessController Process { get; }

        internal UpdateHelperHost Host { get; }

        internal string ReadTarget(string relativePath) =>
            File.ReadAllText(Path.Combine(Target, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static void CreateReleaseArchive(
            string archivePath,
            IReadOnlyDictionary<string, string> files)
        {
            using var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Key, CompressionLevel.Fastest);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(file.Value);
            }

            var manifest = CreateManifest(files);
            var manifestEntry = archive.CreateEntry(UpdateFileTransaction.ManifestName, CompressionLevel.Fastest);
            using var manifestWriter = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false));
            manifestWriter.Write(manifest);
        }

        private static void WriteRelease(string root, IReadOnlyDictionary<string, string> files)
        {
            foreach (var file in files)
            {
                Write(root, file.Key, file.Value);
            }

            Write(root, UpdateFileTransaction.ManifestName, CreateManifest(files));
        }

        private static string CreateManifest(IReadOnlyDictionary<string, string> files)
        {
            var inventory = files.Select(file =>
            {
                var bytes = Encoding.UTF8.GetBytes(file.Value);
                return new
                {
                    path = file.Key,
                    bytes = bytes.LongLength,
                    sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                };
            }).ToArray();
            return JsonSerializer.Serialize(new
            {
                validationOnly = false,
                distributionMode = "FrameworkDependent",
                runtimeIdentifier = "win-x64",
                publishSingleFile = false,
                totalFiles = inventory.Length,
                totalBytes = inventory.Sum(file => file.bytes),
                files = inventory,
            });
        }

        private static void Write(string root, string relativePath, string value)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value, new UTF8Encoding(false));
        }
    }

    private sealed class FakeProcessController(ParentProcessWaitStatus waitStatus) : IUpdateProcessController
    {
        internal int WaitCalls { get; private set; }

        internal string? StartedExecutable { get; private set; }

        public Task<(ParentProcessWaitStatus Status, string? TechnicalMessage)> WaitForExitAsync(
            int processId,
            long expectedStartTimeUtcTicks,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            WaitCalls++;
            return Task.FromResult((waitStatus, waitStatus == ParentProcessWaitStatus.TimedOut ? "timeout" : null));
        }

        public (bool Started, string? TechnicalMessage) StartApplication(
            string executablePath,
            string workingDirectory)
        {
            StartedExecutable = executablePath;
            return (true, null);
        }
    }

    private sealed class ThrowAfterExecutableInstall : IUpdateTransactionFaultInjector
    {
        public void ThrowIfRequested(UpdateTransactionCheckpoint checkpoint, string relativePath)
        {
            if (checkpoint == UpdateTransactionCheckpoint.AfterInstall &&
                string.Equals(relativePath, "BakuretsuOsakanaKobo.exe", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Injected helper transaction failure.");
            }
        }
    }
}

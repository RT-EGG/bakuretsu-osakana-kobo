using System.Security.Cryptography;
using System.Text;
using BakuretsuOsakanaKobo.Update;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class UpdateFileTransactionTests
{
    [Fact]
    public async Task ApplyAsync_ReplacesOwnedFilesAndPreservesDataAndUnknownFiles()
    {
        using var fixture = new TransactionFixture();

        var result = await fixture.Transaction.ApplyAsync(
            fixture.Target,
            fixture.Staged,
            fixture.Backup,
            fixture.CurrentFiles,
            fixture.NextFiles);

        Assert.Equal(UpdateFileTransactionStatus.Succeeded, result.Status);
        Assert.Equal("new executable", fixture.ReadTarget("BakuretsuOsakanaKobo.exe"));
        Assert.Equal("new library", fixture.ReadTarget("new.dll"));
        Assert.Equal("new manifest", fixture.ReadTarget(UpdateFileTransaction.ManifestName));
        Assert.False(File.Exists(Path.Combine(fixture.Target, "old.dll")));
        Assert.Equal("settings", fixture.ReadTarget("data/settings.json"));
        Assert.Equal("user file", fixture.ReadTarget("notes.txt"));
        Assert.False(Directory.Exists(fixture.Backup));
        Assert.Empty(Directory.EnumerateFiles(fixture.Target, "*.update.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("AfterInstall", "BakuretsuOsakanaKobo.exe")]
    [InlineData("AfterDeleteObsolete", "old.dll")]
    public async Task ApplyAsync_WhenMutationFails_RestoresCompleteOldInstallation(
        string checkpointName,
        string path)
    {
        var checkpoint = Enum.Parse<UpdateTransactionCheckpoint>(checkpointName);
        using var fixture = new TransactionFixture(new ThrowAt(checkpoint, path));

        var result = await fixture.Transaction.ApplyAsync(
            fixture.Target,
            fixture.Staged,
            fixture.Backup,
            fixture.CurrentFiles,
            fixture.NextFiles);

        Assert.Equal(UpdateFileTransactionStatus.FailedRolledBack, result.Status);
        Assert.Equal("old executable", fixture.ReadTarget("BakuretsuOsakanaKobo.exe"));
        Assert.Equal("old library", fixture.ReadTarget("old.dll"));
        Assert.Equal("old manifest", fixture.ReadTarget(UpdateFileTransaction.ManifestName));
        Assert.False(File.Exists(Path.Combine(fixture.Target, "new.dll")));
        Assert.Equal("settings", fixture.ReadTarget("data/settings.json"));
        Assert.Equal("user file", fixture.ReadTarget("notes.txt"));
        Assert.False(Directory.Exists(fixture.Backup));
    }

    [Fact]
    public async Task ApplyAsync_WhenBackupFails_DoesNotStartMutation()
    {
        using var fixture = new TransactionFixture(
            new ThrowAt(UpdateTransactionCheckpoint.AfterBackup, "BakuretsuOsakanaKobo.exe"));

        var result = await fixture.Transaction.ApplyAsync(
            fixture.Target,
            fixture.Staged,
            fixture.Backup,
            fixture.CurrentFiles,
            fixture.NextFiles);

        Assert.Equal(UpdateFileTransactionStatus.ValidationFailed, result.Status);
        Assert.Equal("old executable", fixture.ReadTarget("BakuretsuOsakanaKobo.exe"));
        Assert.Equal("old library", fixture.ReadTarget("old.dll"));
        Assert.False(File.Exists(Path.Combine(fixture.Target, "new.dll")));
        Assert.False(Directory.Exists(fixture.Backup));
    }

    [Fact]
    public async Task ApplyAsync_WithCorruptStagedFile_RejectsBeforeBackup()
    {
        using var fixture = new TransactionFixture();
        File.WriteAllText(Path.Combine(fixture.Staged, "new.dll"), "tampered");

        var result = await fixture.Transaction.ApplyAsync(
            fixture.Target,
            fixture.Staged,
            fixture.Backup,
            fixture.CurrentFiles,
            fixture.NextFiles);

        Assert.Equal(UpdateFileTransactionStatus.ValidationFailed, result.Status);
        Assert.Contains("size", result.TechnicalMessage);
        Assert.Equal("old executable", fixture.ReadTarget("BakuretsuOsakanaKobo.exe"));
        Assert.False(Directory.Exists(fixture.Backup));
    }

    [Fact]
    public async Task ApplyAsync_WhenCancelledBeforeMutation_LeavesTargetUnchanged()
    {
        using var fixture = new TransactionFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await fixture.Transaction.ApplyAsync(
            fixture.Target,
            fixture.Staged,
            fixture.Backup,
            fixture.CurrentFiles,
            fixture.NextFiles,
            cancellation.Token);

        Assert.Equal(UpdateFileTransactionStatus.Cancelled, result.Status);
        Assert.Equal("old executable", fixture.ReadTarget("BakuretsuOsakanaKobo.exe"));
        Assert.Equal("old library", fixture.ReadTarget("old.dll"));
        Assert.False(Directory.Exists(fixture.Backup));
    }

    [Theory]
    [InlineData("data/settings.json")]
    [InlineData("../outside.dll")]
    [InlineData("CON")]
    [InlineData("folder\\file.dll")]
    public async Task ApplyAsync_WithUnsafeNextPath_RejectsWithoutChanges(string path)
    {
        using var fixture = new TransactionFixture();
        var unsafeFiles = fixture.NextFiles.Append(
            new UpdateFileDescriptor(path, 0, new string('0', 64))).ToArray();

        var result = await fixture.Transaction.ApplyAsync(
            fixture.Target,
            fixture.Staged,
            fixture.Backup,
            fixture.CurrentFiles,
            unsafeFiles);

        Assert.Equal(UpdateFileTransactionStatus.ValidationFailed, result.Status);
        Assert.Equal("old executable", fixture.ReadTarget("BakuretsuOsakanaKobo.exe"));
        Assert.False(Directory.Exists(fixture.Backup));
    }

    [Fact]
    public async Task ApplyAsync_WithNonEmptyBackupDirectory_RejectsWithoutChanges()
    {
        using var fixture = new TransactionFixture();
        Directory.CreateDirectory(fixture.Backup);
        File.WriteAllText(Path.Combine(fixture.Backup, "unexpected.txt"), "existing");

        var result = await fixture.Transaction.ApplyAsync(
            fixture.Target,
            fixture.Staged,
            fixture.Backup,
            fixture.CurrentFiles,
            fixture.NextFiles);

        Assert.Equal(UpdateFileTransactionStatus.ValidationFailed, result.Status);
        Assert.Equal("old executable", fixture.ReadTarget("BakuretsuOsakanaKobo.exe"));
        Assert.True(File.Exists(Path.Combine(fixture.Backup, "unexpected.txt")));
    }

    [Fact]
    public async Task ApplyAsync_WhenNewOwnedPathCollidesWithUnknownFile_PreservesUnknownFile()
    {
        using var fixture = new TransactionFixture();
        File.WriteAllText(Path.Combine(fixture.Target, "new.dll"), "user collision");

        var result = await fixture.Transaction.ApplyAsync(
            fixture.Target,
            fixture.Staged,
            fixture.Backup,
            fixture.CurrentFiles,
            fixture.NextFiles);

        Assert.Equal(UpdateFileTransactionStatus.ValidationFailed, result.Status);
        Assert.Contains("manifest-external", result.TechnicalMessage);
        Assert.Equal("user collision", File.ReadAllText(Path.Combine(fixture.Target, "new.dll")));
        Assert.Equal("old executable", fixture.ReadTarget("BakuretsuOsakanaKobo.exe"));
        Assert.False(Directory.Exists(fixture.Backup));
    }

    private sealed class TransactionFixture : IDisposable
    {
        private readonly string _root;

        internal TransactionFixture(IUpdateTransactionFaultInjector? faultInjector = null)
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                "BakuretsuOsakanaKobo.Update.Tests",
                Guid.NewGuid().ToString("N"));
            Target = Path.Combine(_root, "target");
            Staged = Path.Combine(_root, "staged");
            Backup = Path.Combine(_root, "backup");
            Directory.CreateDirectory(Target);
            Directory.CreateDirectory(Staged);
            Write(Target, "BakuretsuOsakanaKobo.exe", "old executable");
            Write(Target, "old.dll", "old library");
            Write(Target, UpdateFileTransaction.ManifestName, "old manifest");
            Write(Target, "data/settings.json", "settings");
            Write(Target, "notes.txt", "user file");
            Write(Staged, "BakuretsuOsakanaKobo.exe", "new executable");
            Write(Staged, "new.dll", "new library");
            Write(Staged, UpdateFileTransaction.ManifestName, "new manifest");
            CurrentFiles = ["BakuretsuOsakanaKobo.exe", "old.dll"];
            NextFiles =
            [
                Descriptor(Staged, "BakuretsuOsakanaKobo.exe"),
                Descriptor(Staged, "new.dll"),
            ];
            Transaction = new UpdateFileTransaction(faultInjector);
        }

        internal string Target { get; }

        internal string Staged { get; }

        internal string Backup { get; }

        internal IReadOnlyCollection<string> CurrentFiles { get; }

        internal IReadOnlyCollection<UpdateFileDescriptor> NextFiles { get; }

        internal UpdateFileTransaction Transaction { get; }

        internal string ReadTarget(string relativePath) =>
            File.ReadAllText(Path.Combine(Target, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static UpdateFileDescriptor Descriptor(string root, string relativePath)
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, relativePath));
            return new UpdateFileDescriptor(
                relativePath,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)));
        }

        private static void Write(string root, string relativePath, string value)
        {
            var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value, Encoding.UTF8);
        }
    }

    private sealed class ThrowAt(
        UpdateTransactionCheckpoint checkpoint,
        string relativePath) : IUpdateTransactionFaultInjector
    {
        public void ThrowIfRequested(UpdateTransactionCheckpoint current, string path)
        {
            if (current == checkpoint && string.Equals(path, relativePath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Injected failure after {current}: {path}");
            }
        }
    }
}

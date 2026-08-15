using System.Text;
using System.Text.Json;
using BakuretsuOsakanaKobo.Infrastructure.Persistence;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class RecentFileRepositoryTests
{
    [Fact]
    public async Task RecordSuccessfulOpenAsync_MovesDuplicateToFrontAndPreservesJapaneseWhitespace()
    {
        using var directory = new TestDirectory();
        var firstPath = Path.Combine(directory.Path, "動画", "  日本語 動画  .mp4");
        var secondPath = Path.Combine(directory.Path, "動画", "二本目.wmv");
        using (var repository = CreateRepository(directory.Path))
        {
            await repository.LoadAsync();
            Assert.True((await repository.RecordSuccessfulOpenAsync(firstPath)).Success);
            Assert.True((await repository.RecordSuccessfulOpenAsync(secondPath)).Success);
            Assert.True((await repository.RecordSuccessfulOpenAsync(firstPath.ToUpperInvariant())).Success);

            Assert.Equal(
                [Path.GetFullPath(firstPath.ToUpperInvariant()), Path.GetFullPath(secondPath)],
                repository.GetFiles());
        }

        using var reloadedRepository = CreateRepository(directory.Path);
        var loaded = await reloadedRepository.LoadAsync();
        Assert.False(loaded.UsedDefault, loaded.Warning);
        Assert.Equal(
            [Path.GetFullPath(firstPath.ToUpperInvariant()), Path.GetFullPath(secondPath)],
            reloadedRepository.GetFiles());
    }

    [Fact]
    public async Task RecordSuccessfulOpenAsync_KeepsOnlyTenMostRecentFiles()
    {
        using var directory = new TestDirectory();
        using var repository = CreateRepository(directory.Path);
        await repository.LoadAsync();
        var paths = Enumerable.Range(0, 12)
            .Select(index => Path.Combine(directory.Path, $"video-{index:D2}.mp4"))
            .ToArray();

        foreach (var path in paths)
        {
            Assert.True((await repository.RecordSuccessfulOpenAsync(path)).Success);
        }

        Assert.Equal(
            paths.Reverse().Take(RecentFileDocument.MaximumFiles),
            repository.GetFiles());

        using var reloadedRepository = CreateRepository(directory.Path);
        Assert.False((await reloadedRepository.LoadAsync()).UsedDefault);
        Assert.Equal(
            paths.Reverse().Take(RecentFileDocument.MaximumFiles),
            reloadedRepository.GetFiles());
    }

    [Fact]
    public async Task LoadAsync_BacksUpInvalidDocumentAndUsesEmptyHistory()
    {
        using var directory = new TestDirectory();
        var filePath = Path.Combine(directory.Path, "data", "recent-files.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(
            filePath,
            "{\"SchemaVersion\":1,\"Files\":[\"relative.mp4\"]}",
            Encoding.UTF8);
        using var repository = new RecentFileRepository(filePath);

        var loaded = await repository.LoadAsync();

        Assert.True(loaded.UsedDefault);
        Assert.NotNull(loaded.Warning);
        Assert.NotNull(loaded.CorruptBackupPath);
        Assert.True(File.Exists(loaded.CorruptBackupPath));
        Assert.Empty(repository.GetFiles());
    }

    [Fact]
    public async Task LoadAsync_RejectsDuplicateOrOversizedHistory()
    {
        using var directory = new TestDirectory();
        var filePath = Path.Combine(directory.Path, "data", "recent-files.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var duplicatePath = Path.Combine(directory.Path, "sample.mp4");
        await File.WriteAllBytesAsync(
            filePath,
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                SchemaVersion = 1,
                Files = new[] { duplicatePath, duplicatePath.ToUpperInvariant() },
            }));
        using (var duplicateRepository = new RecentFileRepository(filePath))
        {
            Assert.True((await duplicateRepository.LoadAsync()).UsedDefault);
            Assert.Empty(duplicateRepository.GetFiles());
        }

        await File.WriteAllBytesAsync(
            filePath,
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                SchemaVersion = 1,
                Files = Enumerable.Range(0, 11)
                    .Select(index => Path.Combine(directory.Path, $"video-{index}.mp4")),
            }));
        using var oversizedRepository = new RecentFileRepository(filePath);
        Assert.True((await oversizedRepository.LoadAsync()).UsedDefault);
        Assert.Empty(oversizedRepository.GetFiles());
    }

    [Fact]
    public async Task RecordSuccessfulOpenAsync_WhenReplacementFails_PreservesDiskAndKeepsMemoryForRetry()
    {
        using var directory = new TestDirectory();
        var filePath = Path.Combine(directory.Path, "data", "recent-files.json");
        var firstPath = Path.Combine(directory.Path, "first.mp4");
        var secondPath = Path.Combine(directory.Path, "second.wmv");
        using var repository = new RecentFileRepository(filePath);
        await repository.LoadAsync();
        Assert.True((await repository.RecordSuccessfulOpenAsync(firstPath)).Success);
        var originalBytes = await File.ReadAllBytesAsync(filePath);

        await using (var lockedFile = new FileStream(
                         filePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            var failed = await repository.RecordSuccessfulOpenAsync(secondPath);

            Assert.False(failed.Success);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(filePath));
            Assert.Equal([Path.GetFullPath(secondPath), Path.GetFullPath(firstPath)], repository.GetFiles());
            Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(filePath)!, ".recent-files.json.*.tmp"));
        }

        Assert.True((await repository.SaveAsync()).Success);
        using var reloadedRepository = new RecentFileRepository(filePath);
        Assert.False((await reloadedRepository.LoadAsync()).UsedDefault);
        Assert.Equal([Path.GetFullPath(secondPath), Path.GetFullPath(firstPath)], reloadedRepository.GetFiles());
    }

    [Fact]
    public async Task RemoveAsync_RemovesOnlyMatchingPathCaseInsensitively()
    {
        using var directory = new TestDirectory();
        var firstPath = Path.Combine(directory.Path, "first.mp4");
        var secondPath = Path.Combine(directory.Path, "second.wmv");
        using var repository = CreateRepository(directory.Path);
        await repository.LoadAsync();
        Assert.True((await repository.RecordSuccessfulOpenAsync(firstPath)).Success);
        Assert.True((await repository.RecordSuccessfulOpenAsync(secondPath)).Success);

        Assert.True((await repository.RemoveAsync(firstPath.ToUpperInvariant())).Success);

        Assert.Equal([Path.GetFullPath(secondPath)], repository.GetFiles());
        using var reloadedRepository = CreateRepository(directory.Path);
        Assert.False((await reloadedRepository.LoadAsync()).UsedDefault);
        Assert.Equal([Path.GetFullPath(secondPath)], reloadedRepository.GetFiles());
    }

    [Fact]
    public async Task RemoveMissingAsync_KeepsExistingFilesAndRemovesAllMissingFiles()
    {
        using var directory = new TestDirectory();
        var existingPath = Path.Combine(directory.Path, "existing.mp4");
        var firstMissingPath = Path.Combine(directory.Path, "missing-1.mp4");
        var secondMissingPath = Path.Combine(directory.Path, "missing-2.wmv");
        await File.WriteAllTextAsync(existingPath, string.Empty);
        using var repository = CreateRepository(directory.Path);
        await repository.LoadAsync();
        Assert.True((await repository.RecordSuccessfulOpenAsync(firstMissingPath)).Success);
        Assert.True((await repository.RecordSuccessfulOpenAsync(existingPath)).Success);
        Assert.True((await repository.RecordSuccessfulOpenAsync(secondMissingPath)).Success);

        Assert.True((await repository.RemoveMissingAsync()).Success);

        Assert.Equal([Path.GetFullPath(existingPath)], repository.GetFiles());
        using var reloadedRepository = CreateRepository(directory.Path);
        Assert.False((await reloadedRepository.LoadAsync()).UsedDefault);
        Assert.Equal([Path.GetFullPath(existingPath)], reloadedRepository.GetFiles());
    }

    [Fact]
    public async Task ClearAsync_RemovesAndPersistsAllHistory()
    {
        using var directory = new TestDirectory();
        using var repository = CreateRepository(directory.Path);
        await repository.LoadAsync();
        Assert.True((await repository.RecordSuccessfulOpenAsync(
            Path.Combine(directory.Path, "video.mp4"))).Success);

        Assert.True((await repository.ClearAsync()).Success);

        Assert.Empty(repository.GetFiles());
        using var reloadedRepository = CreateRepository(directory.Path);
        Assert.False((await reloadedRepository.LoadAsync()).UsedDefault);
        Assert.Empty(reloadedRepository.GetFiles());
    }

    private static RecentFileRepository CreateRepository(string directory) =>
        new(Path.Combine(directory, "data", "recent-files.json"));

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "BakuretsuOsakanaKobo.RecentFileTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

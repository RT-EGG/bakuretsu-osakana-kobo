using System.Text;
using BakuretsuOsakanaKobo.Infrastructure.Persistence;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class PlaylistRepositoryTests
{
    [Fact]
    public async Task LoadAsync_DefaultPlaylistIsEmptyWithLoopOff()
    {
        using var directory = new TestDirectory();
        using var repository = CreateRepository(directory.Path);

        var result = await repository.LoadAsync();

        Assert.True(result.UsedDefault);
        Assert.Empty(repository.GetSnapshot().Entries);
        Assert.False(repository.GetSnapshot().Loop);
    }

    [Fact]
    public async Task ReplaceEntriesAsync_PreservesOrderDuplicatesJapaneseAndWhitespace()
    {
        using var directory = new TestDirectory();
        var first = Path.Combine(directory.Path, "動画", "  日本語 動画  .mp4");
        var second = Path.Combine(directory.Path, "動画", "二本目.wmv");
        var entries = new[] { first, second, first };
        using (var repository = CreateRepository(directory.Path))
        {
            await repository.LoadAsync();
            Assert.True((await repository.ReplaceEntriesAsync(entries)).Success);
            Assert.True((await repository.SetLoopAsync(true)).Success);
        }

        using var reloaded = CreateRepository(directory.Path);
        Assert.False((await reloaded.LoadAsync()).UsedDefault);
        Assert.Equal(entries.Select(Path.GetFullPath), reloaded.GetSnapshot().Entries);
        Assert.True(reloaded.GetSnapshot().Loop);
    }

    [Fact]
    public async Task LoadAsync_BacksUpInvalidRelativePathAndUsesDefault()
    {
        using var directory = new TestDirectory();
        var filePath = Path.Combine(directory.Path, "data", "playlist.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(
            filePath,
            "{\"SchemaVersion\":1,\"Entries\":[\"relative.mp4\"],\"Loop\":true}",
            Encoding.UTF8);
        using var repository = new PlaylistRepository(filePath);

        var result = await repository.LoadAsync();

        Assert.True(result.UsedDefault);
        Assert.NotNull(result.CorruptBackupPath);
        Assert.True(File.Exists(result.CorruptBackupPath));
        Assert.Empty(repository.GetSnapshot().Entries);
        Assert.False(repository.GetSnapshot().Loop);
    }

    [Fact]
    public async Task SetLoopAsync_WhenReplacementFails_PreservesDiskAndKeepsMemoryForRetry()
    {
        using var directory = new TestDirectory();
        var filePath = Path.Combine(directory.Path, "data", "playlist.json");
        using var repository = new PlaylistRepository(filePath);
        await repository.LoadAsync();
        Assert.True((await repository.SetLoopAsync(false)).Success);
        var originalBytes = await File.ReadAllBytesAsync(filePath);

        await using (var lockedFile = new FileStream(
                         filePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            var failed = await repository.SetLoopAsync(true);

            Assert.False(failed.Success);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(filePath));
            Assert.True(repository.GetSnapshot().Loop);
            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(filePath)!,
                ".playlist.json.*.tmp"));
        }

        Assert.True((await repository.SetLoopAsync(true)).Success);
        using var reloaded = new PlaylistRepository(filePath);
        Assert.False((await reloaded.LoadAsync()).UsedDefault);
        Assert.True(reloaded.GetSnapshot().Loop);
    }

    private static PlaylistRepository CreateRepository(string directory) =>
        new(Path.Combine(directory, "data", "playlist.json"));

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "BakuretsuOsakanaKobo.PlaylistTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

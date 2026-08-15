using BakuretsuOsakanaKobo.Infrastructure.Persistence;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class AppSettingsRepositoryTests
{
    [Fact]
    public async Task LoadAsync_DefaultsThumbnailIntervalToOnePercent()
    {
        using var directory = new TestDirectory();
        using var repository = CreateRepository(directory.Path);

        var result = await repository.LoadAsync();

        Assert.True(result.UsedDefault);
        Assert.Equal(1.0, repository.GetSnapshot().ThumbnailIntervalPercent);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(5.0)]
    public async Task SetThumbnailIntervalPercentAsync_RoundTripsQuarterPercentValues(double percent)
    {
        using var directory = new TestDirectory();
        var filePath = Path.Combine(directory.Path, "data", "settings.json");
        using (var repository = new AppSettingsRepository(filePath))
        {
            await repository.LoadAsync();
            Assert.True((await repository.SetThumbnailIntervalPercentAsync(percent)).Success);
        }

        using var reloaded = new AppSettingsRepository(filePath);
        Assert.False((await reloaded.LoadAsync()).UsedDefault);
        Assert.Equal(percent, reloaded.GetSnapshot().ThumbnailIntervalPercent);
    }

    [Fact]
    public async Task SetThumbnailIntervalPercentAsync_UpdatesSessionValueBeforeSaveCompletes()
    {
        using var directory = new TestDirectory();
        using var repository = CreateRepository(directory.Path);
        await repository.LoadAsync();

        var saveTask = repository.SetThumbnailIntervalPercentAsync(2.25);

        Assert.Equal(2.25, repository.GetSnapshot().ThumbnailIntervalPercent);
        Assert.True((await saveTask).Success);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.3)]
    [InlineData(5.25)]
    public async Task SetThumbnailIntervalPercentAsync_RejectsOutOfRangeOrOffStepValues(double percent)
    {
        using var directory = new TestDirectory();
        using var repository = CreateRepository(directory.Path);
        await repository.LoadAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.SetThumbnailIntervalPercentAsync(percent));

        Assert.Equal(1.0, repository.GetSnapshot().ThumbnailIntervalPercent);
    }

    [Fact]
    public async Task LoadAsync_OlderSchemaOneDocumentUsesBackwardCompatibleDefault()
    {
        using var directory = new TestDirectory();
        var filePath = Path.Combine(directory.Path, "data", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "{\"SchemaVersion\":1}");
        using var repository = new AppSettingsRepository(filePath);

        var result = await repository.LoadAsync();

        Assert.Equal(AppSettings.CurrentSchemaVersion, result.Value.SchemaVersion);
        Assert.Null(result.Value.ThumbnailIntervalPercent);
        Assert.Null(result.Warning);
        Assert.Null(result.CorruptBackupPath);
        Assert.False(result.UsedDefault);
        Assert.Equal(1.0, repository.GetSnapshot().ThumbnailIntervalPercent);
    }

    [Fact]
    public async Task LoadAsync_BacksUpInvalidIntervalAndUsesDefault()
    {
        using var directory = new TestDirectory();
        var filePath = Path.Combine(directory.Path, "data", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(
            filePath,
            "{\"SchemaVersion\":1,\"ThumbnailIntervalPercent\":0.3}");
        using var repository = new AppSettingsRepository(filePath);

        var result = await repository.LoadAsync();

        Assert.True(result.UsedDefault);
        Assert.NotNull(result.CorruptBackupPath);
        Assert.True(File.Exists(result.CorruptBackupPath));
        Assert.Equal(1.0, repository.GetSnapshot().ThumbnailIntervalPercent);
    }

    [Fact]
    public async Task SetThumbnailIntervalPercentAsync_WhenReplacementFails_PreservesDiskAndKeepsMemoryForRetry()
    {
        using var directory = new TestDirectory();
        var filePath = Path.Combine(directory.Path, "data", "settings.json");
        using var repository = new AppSettingsRepository(filePath);
        await repository.LoadAsync();
        Assert.True((await repository.SaveAsync()).Success);
        var originalBytes = await File.ReadAllBytesAsync(filePath);

        await using (var lockedFile = new FileStream(
                         filePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            var failed = await repository.SetThumbnailIntervalPercentAsync(2.25);

            Assert.False(failed.Success);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(filePath));
            Assert.Equal(2.25, repository.GetSnapshot().ThumbnailIntervalPercent);
            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(filePath)!,
                ".settings.json.*.tmp"));
        }

        Assert.True((await repository.SaveAsync()).Success);
        using var reloaded = new AppSettingsRepository(filePath);
        Assert.False((await reloaded.LoadAsync()).UsedDefault);
        Assert.Equal(2.25, reloaded.GetSnapshot().ThumbnailIntervalPercent);
    }

    private static AppSettingsRepository CreateRepository(string directory) =>
        new(Path.Combine(directory, "data", "settings.json"));

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "BakuretsuOsakanaKobo.AppSettingsTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

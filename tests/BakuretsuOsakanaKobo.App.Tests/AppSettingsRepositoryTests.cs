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
        Assert.Equal(15, repository.GetSnapshot().ThumbnailPreviewWidthPercent);
        Assert.Null(repository.GetSnapshot().LastAutomaticUpdateCheckAttemptUtc);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(30)]
    public async Task SetThumbnailSettingsAsync_RoundTripsWholePreviewWidthPercent(double percent)
    {
        using var directory = new TestDirectory();
        var filePath = Path.Combine(directory.Path, "data", "settings.json");
        using (var repository = new AppSettingsRepository(filePath))
        {
            await repository.LoadAsync();
            Assert.True((await repository.SetThumbnailSettingsAsync(1.25, percent)).Success);
        }

        using var reloaded = new AppSettingsRepository(filePath);
        Assert.False((await reloaded.LoadAsync()).UsedDefault);
        Assert.Equal(1.25, reloaded.GetSnapshot().ThumbnailIntervalPercent);
        Assert.Equal(percent, reloaded.GetSnapshot().ThumbnailPreviewWidthPercent);
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
        Assert.Null(result.Value.ThumbnailPreviewWidthPercent);
        Assert.Null(result.Warning);
        Assert.Null(result.CorruptBackupPath);
        Assert.False(result.UsedDefault);
        Assert.Equal(1.0, repository.GetSnapshot().ThumbnailIntervalPercent);
        Assert.Equal(15, repository.GetSnapshot().ThumbnailPreviewWidthPercent);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5.5)]
    [InlineData(31)]
    public async Task SetThumbnailSettingsAsync_RejectsInvalidPreviewWidth(double percent)
    {
        using var directory = new TestDirectory();
        using var repository = CreateRepository(directory.Path);
        await repository.LoadAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repository.SetThumbnailSettingsAsync(1, percent));

        Assert.Equal(15, repository.GetSnapshot().ThumbnailPreviewWidthPercent);
    }

    [Fact]
    public async Task LoadAsync_BacksUpInvalidPreviewWidthAndUsesDefault()
    {
        using var directory = new TestDirectory();
        var filePath = Path.Combine(directory.Path, "data", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(
            filePath,
            "{\"SchemaVersion\":1,\"ThumbnailIntervalPercent\":1,\"ThumbnailPreviewWidthPercent\":15.5}");
        using var repository = new AppSettingsRepository(filePath);

        var result = await repository.LoadAsync();

        Assert.True(result.UsedDefault);
        Assert.NotNull(result.CorruptBackupPath);
        Assert.True(File.Exists(result.CorruptBackupPath));
        Assert.Equal(15, repository.GetSnapshot().ThumbnailPreviewWidthPercent);
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

    [Fact]
    public async Task RecordAutomaticUpdateCheckAttemptAsync_RoundTripsAndPreservesThumbnailSettings()
    {
        using var directory = new TestDirectory();
        var filePath = Path.Combine(directory.Path, "data", "settings.json");
        var attemptedAt = new DateTimeOffset(2026, 8, 23, 12, 34, 56, TimeSpan.Zero);
        using (var repository = new AppSettingsRepository(filePath))
        {
            await repository.LoadAsync();
            Assert.True((await repository.SetThumbnailSettingsAsync(2.25, 20)).Success);
            Assert.True((await repository.RecordAutomaticUpdateCheckAttemptAsync(attemptedAt)).Success);
        }

        using var reloaded = new AppSettingsRepository(filePath);
        Assert.False((await reloaded.LoadAsync()).UsedDefault);
        var snapshot = reloaded.GetSnapshot();
        Assert.Equal(2.25, snapshot.ThumbnailIntervalPercent);
        Assert.Equal(20, snapshot.ThumbnailPreviewWidthPercent);
        Assert.Equal(attemptedAt, snapshot.LastAutomaticUpdateCheckAttemptUtc);
    }

    [Fact]
    public async Task RecordAutomaticUpdateCheckAttemptAsync_RejectsNonUtcTimestamp()
    {
        using var directory = new TestDirectory();
        using var repository = CreateRepository(directory.Path);
        await repository.LoadAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repository.RecordAutomaticUpdateCheckAttemptAsync(
                new DateTimeOffset(2026, 8, 23, 21, 0, 0, TimeSpan.FromHours(9))));

        Assert.Null(repository.GetSnapshot().LastAutomaticUpdateCheckAttemptUtc);
    }

    [Fact]
    public async Task SetThumbnailSettingsAsync_WhenReplacementFails_PreservesDiskAndBothMemoryValuesForRetry()
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
            var failed = await repository.SetThumbnailSettingsAsync(2.25, 20);

            Assert.False(failed.Success);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(filePath));
            Assert.Equal(2.25, repository.GetSnapshot().ThumbnailIntervalPercent);
            Assert.Equal(20, repository.GetSnapshot().ThumbnailPreviewWidthPercent);
            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(filePath)!,
                ".settings.json.*.tmp"));
        }

        Assert.True((await repository.SaveAsync()).Success);
        using var reloaded = new AppSettingsRepository(filePath);
        Assert.False((await reloaded.LoadAsync()).UsedDefault);
        Assert.Equal(2.25, reloaded.GetSnapshot().ThumbnailIntervalPercent);
        Assert.Equal(20, reloaded.GetSnapshot().ThumbnailPreviewWidthPercent);
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

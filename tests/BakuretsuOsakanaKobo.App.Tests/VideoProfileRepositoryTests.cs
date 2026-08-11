using System.Text;
using System.Text.Json;
using BakuretsuOsakanaKobo.Infrastructure.Persistence;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class VideoProfileRepositoryTests
{
    [Fact]
    public async Task SaveAndLoadAsync_RoundTripsNormalizedPathVolumeMuteAndStartPosition()
    {
        using var directory = new TestDirectory();
        var filePath = Path.Combine(directory.Path, "data", "video-profiles.json");
        var videoPath = Path.Combine(directory.Path, "videos", "sample.mp4");
        using (var repository = new VideoProfileRepository(filePath))
        {
            await repository.LoadAsync();
            repository.Set(videoPath, 350, isMuted: true);
            repository.SetStartPosition(videoPath, 12_345);
            var saved = await repository.SaveAsync();
            Assert.True(saved.Success, saved.ErrorMessage);
        }

        using var reloadedRepository = new VideoProfileRepository(filePath);
        var loaded = await reloadedRepository.LoadAsync();

        Assert.False(loaded.UsedDefault, loaded.Warning);
        Assert.True(reloadedRepository.TryGet(videoPath, out var profile));
        Assert.Equal(Path.GetFullPath(videoPath), profile.VideoPath);
        Assert.Equal(350, profile.VolumePercent);
        Assert.True(profile.IsMuted);
        Assert.Equal(12_345, profile.StartPositionMilliseconds);
    }

    [Fact]
    public async Task TryGet_IsCaseInsensitiveAndMovedPathIsDistinct()
    {
        using var directory = new TestDirectory();
        using var repository = CreateRepository(directory.Path);
        await repository.LoadAsync();
        var originalPath = Path.Combine(directory.Path, "Video", "Sample.MP4");
        repository.Set(originalPath, 65, isMuted: false);

        var alternateCase = originalPath.ToUpperInvariant();
        var movedPath = Path.Combine(directory.Path, "Moved", "Sample.MP4");

        Assert.True(repository.TryGet(alternateCase, out var profile));
        Assert.Equal(65, profile.VolumePercent);
        Assert.False(repository.TryGet(movedPath, out _));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(501, 500)]
    public async Task Set_ClampsVolumeToProductRange(int requested, int expected)
    {
        using var directory = new TestDirectory();
        using var repository = CreateRepository(directory.Path);
        await repository.LoadAsync();
        var videoPath = Path.Combine(directory.Path, "sample.mp4");

        repository.Set(videoPath, requested, isMuted: false);

        Assert.True(repository.TryGet(videoPath, out var profile));
        Assert.Equal(expected, profile.VolumePercent);
    }

    [Fact]
    public async Task AudioAndStartPositionUpdates_PreserveEachOther()
    {
        using var directory = new TestDirectory();
        using var repository = CreateRepository(directory.Path);
        await repository.LoadAsync();
        var videoPath = Path.Combine(directory.Path, "sample.mp4");

        repository.SetStartPosition(videoPath, 15_000);
        repository.Set(videoPath, 275, isMuted: true);
        Assert.True(repository.TryGet(videoPath, out var afterAudio));
        Assert.Equal(15_000, afterAudio.StartPositionMilliseconds);

        repository.SetStartPosition(videoPath, 30_000);
        Assert.True(repository.TryGet(videoPath, out var afterStartPosition));
        Assert.Equal(275, afterStartPosition.VolumePercent);
        Assert.True(afterStartPosition.IsMuted);
        Assert.Equal(30_000, afterStartPosition.StartPositionMilliseconds);
    }

    [Fact]
    public async Task SetStartPosition_RejectsNegativeValue()
    {
        using var directory = new TestDirectory();
        using var repository = CreateRepository(directory.Path);
        await repository.LoadAsync();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            repository.SetStartPosition(Path.Combine(directory.Path, "sample.mp4"), -1));
    }

    [Fact]
    public async Task LoadAsync_AcceptsExistingSchemaWithoutStartPosition()
    {
        using var directory = new TestDirectory();
        var filePath = Path.Combine(directory.Path, "data", "video-profiles.json");
        var videoPath = Path.Combine(directory.Path, "sample.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(
            filePath,
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                SchemaVersion = 1,
                Profiles = new[]
                {
                    new { VideoPath = videoPath, VolumePercent = 125, IsMuted = true },
                },
            }));
        using var repository = new VideoProfileRepository(filePath);

        var loaded = await repository.LoadAsync();

        Assert.False(loaded.UsedDefault, loaded.Warning);
        Assert.True(repository.TryGet(videoPath, out var profile));
        Assert.Null(profile.StartPositionMilliseconds);
    }

    [Fact]
    public async Task LoadAsync_RejectsNegativeStartPosition()
    {
        using var directory = new TestDirectory();
        var filePath = Path.Combine(directory.Path, "data", "video-profiles.json");
        var videoPath = Path.Combine(directory.Path, "sample.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllBytesAsync(
            filePath,
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                SchemaVersion = 1,
                Profiles = new[]
                {
                    new
                    {
                        VideoPath = videoPath,
                        VolumePercent = 100,
                        IsMuted = false,
                        StartPositionMilliseconds = -1,
                    },
                },
            }));
        using var repository = new VideoProfileRepository(filePath);

        var loaded = await repository.LoadAsync();

        Assert.True(loaded.UsedDefault);
        Assert.NotNull(loaded.CorruptBackupPath);
        Assert.False(repository.TryGet(videoPath, out _));
    }

    [Fact]
    public async Task LoadAsync_BacksUpInvalidDocumentAndUsesEmptyDefaults()
    {
        using var directory = new TestDirectory();
        var filePath = Path.Combine(directory.Path, "data", "video-profiles.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(
            filePath,
            "{\"SchemaVersion\":1,\"Profiles\":[{\"VideoPath\":\"relative.mp4\",\"VolumePercent\":100}]}",
            Encoding.UTF8);
        using var repository = new VideoProfileRepository(filePath);

        var loaded = await repository.LoadAsync();

        Assert.True(loaded.UsedDefault);
        Assert.NotNull(loaded.Warning);
        Assert.NotNull(loaded.CorruptBackupPath);
        Assert.True(File.Exists(loaded.CorruptBackupPath));
        Assert.False(repository.TryGet(Path.Combine(directory.Path, "relative.mp4"), out _));
    }

    [Fact]
    public async Task SaveAsync_WhenReplacementFails_PreservesPreviousDocument()
    {
        using var directory = new TestDirectory();
        var filePath = Path.Combine(directory.Path, "data", "video-profiles.json");
        var videoPath = Path.Combine(directory.Path, "sample.mp4");
        using var repository = new VideoProfileRepository(filePath);
        await repository.LoadAsync();
        repository.Set(videoPath, 100, isMuted: false);
        Assert.True((await repository.SaveAsync()).Success);
        var originalBytes = await File.ReadAllBytesAsync(filePath);
        repository.Set(videoPath, 500, isMuted: true);

        await using var lockedFile = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var failed = await repository.SaveAsync();

        Assert.False(failed.Success);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(filePath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(filePath)!, ".video-profiles.json.*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_WritesDeterministicSchemaAndDistinctProfiles()
    {
        using var directory = new TestDirectory();
        var filePath = Path.Combine(directory.Path, "data", "video-profiles.json");
        using var repository = new VideoProfileRepository(filePath);
        await repository.LoadAsync();
        repository.Set(Path.Combine(directory.Path, "b.wmv"), 80, isMuted: true);
        repository.Set(Path.Combine(directory.Path, "A.mp4"), 120, isMuted: false);

        Assert.True((await repository.SaveAsync()).Success);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(filePath));
        var profiles = document.RootElement.GetProperty("Profiles");

        Assert.Equal(VideoProfileDocument.CurrentSchemaVersion, document.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.Equal(2, profiles.GetArrayLength());
        Assert.EndsWith("A.mp4", profiles[0].GetProperty("VideoPath").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("b.wmv", profiles[1].GetProperty("VideoPath").GetString(), StringComparison.Ordinal);
    }

    private static VideoProfileRepository CreateRepository(string directory) =>
        new(Path.Combine(directory, "data", "video-profiles.json"));

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "BakuretsuOsakanaKobo.VideoProfileTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

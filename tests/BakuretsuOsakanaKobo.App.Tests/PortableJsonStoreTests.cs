using System.Text;
using BakuretsuOsakanaKobo.Infrastructure.Persistence;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class PortableJsonStoreTests
{
    [Fact]
    public void PortableDataPaths_UsesExecutableDirectoryDataFolder()
    {
        using var directory = new TestDirectory();

        var paths = new PortableDataPaths(directory.Path);

        Assert.Equal(System.IO.Path.Combine(directory.Path, "data"), paths.DataDirectory);
        Assert.Equal(System.IO.Path.Combine(directory.Path, "data", "settings.json"), paths.SettingsFilePath);
    }

    [Fact]
    public async Task SaveAndLoadAsync_RoundTripsVersionedSettingsAndReplacesExistingFile()
    {
        using var directory = new TestDirectory();
        var paths = new PortableDataPaths(directory.Path);
        var store = CreateStore(paths.SettingsFilePath);

        var firstSave = await store.SaveAsync(new AppSettings());
        var secondSave = await store.SaveAsync(new AppSettings());
        var loaded = await store.LoadOrDefaultAsync();

        Assert.True(firstSave.Success, firstSave.ErrorMessage);
        Assert.True(secondSave.Success, secondSave.ErrorMessage);
        Assert.False(loaded.UsedDefault);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.Value.SchemaVersion);
        Assert.Empty(Directory.EnumerateFiles(paths.DataDirectory, ".settings.json.*.tmp"));
    }

    [Fact]
    public async Task LoadOrDefaultAsync_BacksUpCorruptJsonAndReturnsDefaults()
    {
        using var directory = new TestDirectory();
        var paths = new PortableDataPaths(directory.Path);
        Directory.CreateDirectory(paths.DataDirectory);
        await File.WriteAllTextAsync(paths.SettingsFilePath, "{ invalid json", Encoding.UTF8);
        var store = CreateStore(paths.SettingsFilePath);

        var loaded = await store.LoadOrDefaultAsync();

        Assert.True(loaded.UsedDefault);
        Assert.NotNull(loaded.Warning);
        Assert.NotNull(loaded.CorruptBackupPath);
        Assert.True(File.Exists(loaded.CorruptBackupPath));
        Assert.False(File.Exists(paths.SettingsFilePath));
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.Value.SchemaVersion);
    }

    [Fact]
    public async Task SaveAsync_WhenReplacementIsDenied_PreservesExistingFileAndDeletesTemporaryFile()
    {
        using var directory = new TestDirectory();
        var paths = new PortableDataPaths(directory.Path);
        var store = CreateStore(paths.SettingsFilePath);
        var initialSave = await store.SaveAsync(new AppSettings());
        Assert.True(initialSave.Success, initialSave.ErrorMessage);
        var originalBytes = await File.ReadAllBytesAsync(paths.SettingsFilePath);

        await using var lockedFile = new FileStream(
            paths.SettingsFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var failedSave = await store.SaveAsync(new AppSettings());

        Assert.False(failedSave.Success);
        Assert.NotNull(failedSave.Exception);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(paths.SettingsFilePath));
        Assert.Empty(Directory.EnumerateFiles(paths.DataDirectory, ".settings.json.*.tmp"));
    }

    [Fact]
    public async Task LoadOrDefaultAsync_WhenSchemaIsUnsupported_BacksUpFileAndReturnsCurrentDefaults()
    {
        using var directory = new TestDirectory();
        var paths = new PortableDataPaths(directory.Path);
        Directory.CreateDirectory(paths.DataDirectory);
        await File.WriteAllTextAsync(paths.SettingsFilePath, "{\"SchemaVersion\":99}");
        var store = CreateStore(paths.SettingsFilePath);

        var loaded = await store.LoadOrDefaultAsync();

        Assert.True(loaded.UsedDefault);
        Assert.NotNull(loaded.CorruptBackupPath);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.Value.SchemaVersion);
    }

    [Fact]
    public async Task LoadOrDefaultAsync_WhenFileIsLocked_ReturnsDefaultsWithWarningWithoutMovingFile()
    {
        using var directory = new TestDirectory();
        var paths = new PortableDataPaths(directory.Path);
        var store = CreateStore(paths.SettingsFilePath);
        var initialSave = await store.SaveAsync(new AppSettings());
        Assert.True(initialSave.Success, initialSave.ErrorMessage);

        await using var lockedFile = new FileStream(
            paths.SettingsFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);
        var loaded = await store.LoadOrDefaultAsync();

        Assert.True(loaded.UsedDefault);
        Assert.NotNull(loaded.Warning);
        Assert.Null(loaded.CorruptBackupPath);
        Assert.True(File.Exists(paths.SettingsFilePath));
    }

    [Fact]
    public async Task SaveAsync_WhenCancelled_DeletesOwnedTemporaryFile()
    {
        using var directory = new TestDirectory();
        var paths = new PortableDataPaths(directory.Path);
        var store = CreateStore(paths.SettingsFilePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveAsync(new AppSettings(), cancellation.Token));

        Assert.Empty(Directory.EnumerateFiles(paths.DataDirectory, ".settings.json.*.tmp"));
    }

    private static PortableJsonStore<AppSettings> CreateStore(string path) =>
        new(path, () => new AppSettings(), AppSettings.IsValid);

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "BakuretsuOsakanaKobo.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

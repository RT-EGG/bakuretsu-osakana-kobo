using System.IO;
using System.Text.Json;

namespace BakuretsuOsakanaKobo.Infrastructure.Persistence;

public sealed class PortableJsonStore<T> where T : class
{
    private readonly Func<T> _createDefault;
    private readonly Func<T, bool> _isValid;
    private readonly JsonSerializerOptions _serializerOptions;

    public PortableJsonStore(
        string filePath,
        Func<T> createDefault,
        Func<T, bool>? isValid = null,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(createDefault);

        FilePath = Path.GetFullPath(filePath);
        DataDirectory = Path.GetDirectoryName(FilePath)
            ?? throw new ArgumentException("The file path must include a directory.", nameof(filePath));
        _createDefault = createDefault;
        _isValid = isValid ?? (_ => true);
        _serializerOptions = serializerOptions ?? new JsonSerializerOptions { WriteIndented = true };
    }

    public string DataDirectory { get; }

    public string FilePath { get; }

    public async Task<JsonSaveResult> SaveAsync(T value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(DataDirectory);
            temporaryPath = Path.Combine(
                DataDirectory,
                $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _serializerOptions);

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(FilePath))
            {
                File.Replace(temporaryPath, FilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, FilePath);
            }

            temporaryPath = null;
            return JsonSaveResult.Succeeded();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return JsonSaveResult.Failed(exception);
        }
        finally
        {
            DeleteOwnedTemporaryFile(temporaryPath);
        }
    }

    public async Task<JsonLoadResult<T>> LoadOrDefaultAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(FilePath, cancellationToken).ConfigureAwait(false);
            var value = JsonSerializer.Deserialize<T>(bytes, _serializerOptions);
            return value is not null && _isValid(value)
                ? JsonLoadResult<T>.Loaded(value)
                : RecoverCorruptFile("The JSON document was null or failed validation.");
        }
        catch (JsonException exception)
        {
            return RecoverCorruptFile(exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return RecoverCorruptFile(exception.Message);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return JsonLoadResult<T>.Default(_createDefault());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return JsonLoadResult<T>.Default(_createDefault(), exception.Message);
        }
    }

    private JsonLoadResult<T> RecoverCorruptFile(string warning)
    {
        string? backupPath = null;
        string? backupWarning = null;
        try
        {
            backupPath = $"{FilePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            File.Move(FilePath, backupPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            backupPath = null;
            backupWarning = exception.Message;
        }

        var combinedWarning = backupWarning is null
            ? warning
            : $"{warning} Corrupt-file backup failed: {backupWarning}";
        return JsonLoadResult<T>.Default(_createDefault(), combinedWarning, backupPath);
    }

    private static void DeleteOwnedTemporaryFile(string? temporaryPath)
    {
        if (temporaryPath is null)
        {
            return;
        }

        try
        {
            File.Delete(temporaryPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed record JsonSaveResult(bool Success, string? ErrorMessage, Exception? Exception)
{
    internal static JsonSaveResult Succeeded() => new(true, null, null);

    internal static JsonSaveResult Failed(Exception exception) => new(false, exception.Message, exception);
}

public sealed record JsonLoadResult<T>(
    T Value,
    bool UsedDefault,
    string? Warning,
    string? CorruptBackupPath) where T : class
{
    internal static JsonLoadResult<T> Loaded(T value) => new(value, false, null, null);

    internal static JsonLoadResult<T> Default(
        T value,
        string? warning = null,
        string? corruptBackupPath = null) => new(value, true, warning, corruptBackupPath);
}

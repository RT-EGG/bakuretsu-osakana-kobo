using System.IO;
using System.Text.Json;

namespace BakuretsuOsakanaKobo.Spikes.SingleInstanceData;

internal sealed class PortableJsonStore<T> where T : class, new()
{
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    internal PortableJsonStore(string executableDirectory, string fileName)
    {
        DataDirectory = Path.Combine(Path.GetFullPath(executableDirectory), "data");
        FilePath = Path.Combine(DataDirectory, fileName);
    }

    internal string DataDirectory { get; }
    internal string FilePath { get; }

    internal SaveResult TrySave(T value)
    {
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(DataDirectory);
            temporaryPath = Path.Combine(
                DataDirectory,
                $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _options);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }

            if (File.Exists(FilePath))
            {
                File.Replace(temporaryPath, FilePath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, FilePath);
            }

            return new SaveResult { Success = true };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SaveResult { Error = exception.Message };
        }
        finally
        {
            if (temporaryPath is not null)
            {
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
    }

    internal LoadResult<T> LoadOrDefault()
    {
        if (!File.Exists(FilePath))
        {
            return new LoadResult<T> { Value = new T(), UsedDefault = true };
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllBytes(FilePath), _options);
            return value is null
                ? RecoverCorruptFile("The JSON document contained null.")
                : new LoadResult<T> { Value = value };
        }
        catch (JsonException exception)
        {
            return RecoverCorruptFile(exception.Message);
        }
        catch (IOException exception)
        {
            return new LoadResult<T> { Value = new T(), UsedDefault = true, Warning = exception.Message };
        }
        catch (UnauthorizedAccessException exception)
        {
            return new LoadResult<T> { Value = new T(), UsedDefault = true, Warning = exception.Message };
        }
    }

    private LoadResult<T> RecoverCorruptFile(string warning)
    {
        string? backupPath = null;
        try
        {
            backupPath = $"{FilePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            File.Move(FilePath, backupPath);
        }
        catch (IOException)
        {
            backupPath = null;
        }
        catch (UnauthorizedAccessException)
        {
            backupPath = null;
        }

        return new LoadResult<T>
        {
            Value = new T(),
            UsedDefault = true,
            Warning = warning,
            CorruptBackupPath = backupPath,
        };
    }
}

internal sealed class SaveResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}

internal sealed class LoadResult<T>
{
    public required T Value { get; set; }
    public bool UsedDefault { get; set; }
    public string? Warning { get; set; }
    public string? CorruptBackupPath { get; set; }
}

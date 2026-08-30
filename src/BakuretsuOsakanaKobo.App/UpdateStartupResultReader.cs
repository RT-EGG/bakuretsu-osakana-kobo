using System.IO;
using System.Text.Json;
using BakuretsuOsakanaKobo.Update;

namespace BakuretsuOsakanaKobo;

internal sealed record UpdateStartupArguments(string[] LaunchArguments, string? ResultPath);

internal sealed record UpdateStartupReadResult(
    UpdateHelperResult? Result,
    string? TechnicalMessage = null);

internal static class UpdateStartupResultReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    internal static UpdateStartupArguments ExtractArguments(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments is ["--update-result", var resultPath]
            ? new UpdateStartupArguments([], resultPath)
            : new UpdateStartupArguments(arguments, null);
    }

    internal static async Task<UpdateStartupReadResult> ReadAsync(
        string? resultPath,
        string workingRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resultPath))
        {
            return new UpdateStartupReadResult(null);
        }

        string? ownedWorkingDirectory = null;
        try
        {
            var fullRoot = Path.GetFullPath(workingRoot);
            var fullResultPath = Path.GetFullPath(resultPath);
            var candidateWorkingDirectory = Path.GetDirectoryName(fullResultPath)!;
            var relativeDirectory = Path.GetRelativePath(fullRoot, candidateWorkingDirectory);
            var directoryName = Path.GetFileName(candidateWorkingDirectory);
            if (!string.Equals(Path.GetFileName(fullResultPath), UpdateHelperHost.ResultFileName, StringComparison.Ordinal) ||
                relativeDirectory.Contains(Path.DirectorySeparatorChar) ||
                relativeDirectory.Contains(Path.AltDirectorySeparatorChar) ||
                !string.Equals(relativeDirectory, directoryName, StringComparison.Ordinal) ||
                directoryName.Length != 39 ||
                !directoryName.StartsWith("update-", StringComparison.Ordinal) ||
                !directoryName[7..].All(Uri.IsHexDigit) ||
                !File.Exists(fullResultPath) ||
                PathContainsReparsePoint(fullResultPath) ||
                new FileInfo(fullResultPath).Length is <= 0 or > 64 * 1024)
            {
                return new UpdateStartupReadResult(null, "The update result path was outside the approved boundary.");
            }

            ownedWorkingDirectory = candidateWorkingDirectory;
            var bytes = await File.ReadAllBytesAsync(fullResultPath, cancellationToken).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<UpdateHelperResult>(bytes, SerializerOptions);
            if (result is null || result.SchemaVersion != 1)
            {
                return new UpdateStartupReadResult(null, "The update result contract was invalid.");
            }

            return new UpdateStartupReadResult(result);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return new UpdateStartupReadResult(null, exception.Message);
        }
        finally
        {
            if (ownedWorkingDirectory is not null)
            {
                _ = CleanupOwnedDirectoryAsync(ownedWorkingDirectory);
            }
        }
    }

    private static async Task CleanupOwnedDirectoryAsync(string workingDirectory)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(workingDirectory))
                {
                    return;
                }

                Directory.Delete(workingDirectory, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }

    private static bool PathContainsReparsePoint(string path)
    {
        var current = Path.GetFullPath(path);
        var root = Path.GetPathRoot(current);
        while (!string.IsNullOrEmpty(current) && !string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            current = Path.GetDirectoryName(current) ?? string.Empty;
        }

        return false;
    }
}

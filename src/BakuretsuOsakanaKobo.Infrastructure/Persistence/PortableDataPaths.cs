using System.IO;

namespace BakuretsuOsakanaKobo.Infrastructure.Persistence;

public sealed class PortableDataPaths
{
    public PortableDataPaths(string executableDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableDirectory);

        ExecutableDirectory = Path.GetFullPath(executableDirectory);
        DataDirectory = Path.Combine(ExecutableDirectory, "data");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        SettingsFilePath = Path.Combine(DataDirectory, "settings.json");
        VideoProfilesFilePath = Path.Combine(DataDirectory, "video-profiles.json");
        RecentFilesFilePath = Path.Combine(DataDirectory, "recent-files.json");
    }

    public string ExecutableDirectory { get; }

    public string DataDirectory { get; }

    public string LogsDirectory { get; }

    public string SettingsFilePath { get; }

    public string VideoProfilesFilePath { get; }

    public string RecentFilesFilePath { get; }

    public static PortableDataPaths ForCurrentProcess() => new(AppContext.BaseDirectory);

    public string GetDataFilePath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Path.IsPathRooted(fileName) ||
            !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw new ArgumentException("A data file name must not contain a directory path.", nameof(fileName));
        }

        return Path.Combine(DataDirectory, fileName);
    }
}

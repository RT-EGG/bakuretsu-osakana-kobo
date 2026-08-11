using System.IO;

namespace BakuretsuOsakanaKobo.Infrastructure.Persistence;

public sealed class VideoProfileDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public List<VideoProfileEntry> Profiles { get; init; } = [];

    public static bool IsValid(VideoProfileDocument document)
    {
        if (document.SchemaVersion != CurrentSchemaVersion || document.Profiles is null)
        {
            return false;
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in document.Profiles)
        {
            if (profile is null ||
                profile.VolumePercent is < VideoProfileEntry.MinimumVolumePercent or > VideoProfileEntry.MaximumVolumePercent ||
                !VideoProfilePath.TryNormalize(profile.VideoPath, out var normalizedPath) ||
                !string.Equals(profile.VideoPath, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                !paths.Add(normalizedPath))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class VideoProfileEntry
{
    public const int DefaultVolumePercent = 100;
    public const int MinimumVolumePercent = 0;
    public const int MaximumVolumePercent = 500;

    public string VideoPath { get; init; } = string.Empty;

    public int VolumePercent { get; init; } = DefaultVolumePercent;

    public bool IsMuted { get; init; }
}

public static class VideoProfilePath
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    internal static bool TryNormalize(string? path, out string normalizedPath)
    {
        try
        {
            normalizedPath = Normalize(path ?? string.Empty);
            return Path.IsPathFullyQualified(normalizedPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalizedPath = string.Empty;
            return false;
        }
    }
}

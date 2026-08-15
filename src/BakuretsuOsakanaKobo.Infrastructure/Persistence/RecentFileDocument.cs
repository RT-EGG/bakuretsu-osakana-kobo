namespace BakuretsuOsakanaKobo.Infrastructure.Persistence;

public sealed class RecentFileDocument
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumFiles = 10;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public List<string> Files { get; init; } = [];

    public static bool IsValid(RecentFileDocument document)
    {
        if (document.SchemaVersion != CurrentSchemaVersion ||
            document.Files is null ||
            document.Files.Count > MaximumFiles)
        {
            return false;
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in document.Files)
        {
            if (!VideoProfilePath.TryNormalize(path, out var normalizedPath) ||
                !string.Equals(path, normalizedPath, StringComparison.Ordinal) ||
                !paths.Add(normalizedPath))
            {
                return false;
            }
        }

        return true;
    }
}

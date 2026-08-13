namespace BakuretsuOsakanaKobo.Infrastructure.Persistence;

public sealed class PlaylistDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public List<string> Entries { get; init; } = [];

    public bool Loop { get; init; }

    public static bool IsValid(PlaylistDocument document)
    {
        if (document.SchemaVersion != CurrentSchemaVersion || document.Entries is null)
        {
            return false;
        }

        foreach (var path in document.Entries)
        {
            if (!VideoProfilePath.TryNormalize(path, out var normalizedPath) ||
                !string.Equals(path, normalizedPath, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

using System.IO;

namespace BakuretsuOsakanaKobo;

internal sealed record PlaylistEntryPresentation(
    int Index,
    int Order,
    string Path,
    string FileName,
    bool IsMissing);

internal static class PlaylistPresentation
{
    public static IReadOnlyList<PlaylistEntryPresentation> From(IEnumerable<string> entries) =>
        From(entries, File.Exists);

    internal static IReadOnlyList<PlaylistEntryPresentation> From(
        IEnumerable<string> entries,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(fileExists);
        return entries
            .Select((path, index) => new PlaylistEntryPresentation(
                index,
                index + 1,
                path,
                Path.GetFileName(path),
                IsMissing: !fileExists(path)))
            .ToArray();
    }
}

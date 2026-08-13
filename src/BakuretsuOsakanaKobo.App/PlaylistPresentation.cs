using System.IO;

namespace BakuretsuOsakanaKobo;

internal sealed record PlaylistEntryPresentation(
    int Index,
    int Order,
    string Path,
    string FileName,
    bool IsMissing,
    bool HasLoadError,
    bool IsCurrent)
{
    public string StatusText => IsMissing
        ? "見つかりません"
        : HasLoadError
            ? "読み込み不能"
            : string.Empty;
}

internal static class PlaylistPresentation
{
    public static IReadOnlyList<PlaylistEntryPresentation> From(IEnumerable<string> entries) =>
        From(entries, File.Exists, currentIndex: null, loadErrorIndices: new HashSet<int>());

    internal static IReadOnlyList<PlaylistEntryPresentation> From(
        IEnumerable<string> entries,
        Func<string, bool> fileExists,
        int? currentIndex = null,
        IReadOnlySet<int>? loadErrorIndices = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(fileExists);
        loadErrorIndices ??= new HashSet<int>();
        return entries
            .Select((path, index) => new PlaylistEntryPresentation(
                index,
                index + 1,
                path,
                Path.GetFileName(path),
                IsMissing: !fileExists(path),
                HasLoadError: loadErrorIndices.Contains(index),
                IsCurrent: currentIndex == index))
            .ToArray();
    }
}

using System.IO;

namespace BakuretsuOsakanaKobo;

internal sealed record RecentFileMenuEntry(string Path, string DisplayName, bool IsMissing);

internal sealed record RecentFileMenuPresentation(
    IReadOnlyList<RecentFileMenuEntry> Files,
    IReadOnlyList<RecentFileMenuEntry> MissingFiles)
{
    public bool ShowRemoveAllMissing => MissingFiles.Count > 1;

    public static RecentFileMenuPresentation From(IEnumerable<string> paths) =>
        From(paths, File.Exists);

    internal static RecentFileMenuPresentation From(
        IEnumerable<string> paths,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileExists);

        var files = paths
            .Select(path => new RecentFileMenuEntry(
                path,
                Path.GetFileName(path),
                IsMissing: !fileExists(path)))
            .ToArray();
        return new RecentFileMenuPresentation(
            files,
            files.Where(file => file.IsMissing).ToArray());
    }
}

using System.IO;

namespace BakuretsuOsakanaKobo;

internal enum FileDropRequestKind
{
    None,
    SingleSupportedFile,
    MultipleFiles,
    UnsupportedFile,
}

internal readonly record struct FileDropRequest(FileDropRequestKind Kind, string? Path = null);

internal static class FileDropRequestClassifier
{
    public static FileDropRequest Classify(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return new FileDropRequest(FileDropRequestKind.None);
        }

        if (paths.Count > 1)
        {
            return new FileDropRequest(FileDropRequestKind.MultipleFiles);
        }

        var path = paths[0];
        if (string.IsNullOrWhiteSpace(path))
        {
            return new FileDropRequest(FileDropRequestKind.UnsupportedFile, path);
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase)
            ? new FileDropRequest(FileDropRequestKind.SingleSupportedFile, path)
            : new FileDropRequest(FileDropRequestKind.UnsupportedFile, path);
    }
}

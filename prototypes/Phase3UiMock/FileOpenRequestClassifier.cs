using System.IO;

namespace BakuretsuOsakanaKobo.Phase3UiMock;

internal enum FileOpenRequestKind
{
    None,
    SingleSupportedFile,
    MultipleFiles,
    UnsupportedFile,
}

internal readonly record struct FileOpenRequest(FileOpenRequestKind Kind, string? Path = null);

internal static class FileOpenRequestClassifier
{
    public static FileOpenRequest Classify(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return new FileOpenRequest(FileOpenRequestKind.None);
        }

        if (paths.Count > 1)
        {
            return new FileOpenRequest(FileOpenRequestKind.MultipleFiles);
        }

        var path = paths[0];
        var extension = Path.GetExtension(path);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase)
                ? new FileOpenRequest(FileOpenRequestKind.SingleSupportedFile, path)
                : new FileOpenRequest(FileOpenRequestKind.UnsupportedFile, path);
    }
}

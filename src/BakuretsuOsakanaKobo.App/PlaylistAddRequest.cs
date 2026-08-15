using System.IO;

namespace BakuretsuOsakanaKobo;

internal sealed record PlaylistAddRequest(IReadOnlyList<string> Paths, int RejectedCount)
{
    public static PlaylistAddRequest From(IEnumerable<string?> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var accepted = new List<string>();
        var rejectedCount = 0;
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                Path.IsPathFullyQualified(path) &&
                IsSupportedVideo(path))
            {
                accepted.Add(path);
            }
            else
            {
                rejectedCount++;
            }
        }

        return new PlaylistAddRequest(accepted, rejectedCount);
    }

    private static bool IsSupportedVideo(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".wmv", StringComparison.OrdinalIgnoreCase);
    }
}

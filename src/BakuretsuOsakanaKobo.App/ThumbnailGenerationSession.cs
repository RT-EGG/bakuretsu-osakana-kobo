using System.IO;
using BakuretsuOsakanaKobo.Infrastructure.Persistence;

namespace BakuretsuOsakanaKobo;

internal sealed record ThumbnailGenerationSession(string VideoPath, double IntervalPercent)
{
    public static ThumbnailGenerationSession Create(string videoPath, double intervalPercent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ThumbnailGenerationInterval.EnsureValid(intervalPercent, nameof(intervalPercent));
        return new ThumbnailGenerationSession(Path.GetFullPath(videoPath), intervalPercent);
    }
}

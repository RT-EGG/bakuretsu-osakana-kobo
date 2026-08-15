namespace BakuretsuOsakanaKobo.Playback;

internal interface IThumbnailFrameExtractor
{
    Task ExtractAsync(
        ThumbnailGenerationRun run,
        Action<ThumbnailFrame> frameReady,
        CancellationToken cancellationToken);
}

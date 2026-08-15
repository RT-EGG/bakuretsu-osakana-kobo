namespace BakuretsuOsakanaKobo.Playback;

public interface IThumbnailGenerationService : IAsyncDisposable
{
    event EventHandler<ThumbnailGenerationErrorEventArgs>? GenerationFailed;

    ThumbnailGenerationRun StartSession(
        string videoPath,
        long durationMilliseconds,
        double intervalPercent);

    Task StopAsync();
}

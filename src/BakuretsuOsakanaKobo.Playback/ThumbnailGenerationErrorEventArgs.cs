namespace BakuretsuOsakanaKobo.Playback;

public sealed class ThumbnailGenerationErrorEventArgs(
    string videoPath,
    Exception exception) : EventArgs
{
    public string VideoPath { get; } = videoPath;

    public Exception Exception { get; } = exception;
}

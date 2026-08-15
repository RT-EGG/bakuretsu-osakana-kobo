namespace BakuretsuOsakanaKobo.Playback;

public sealed record ThumbnailFrame(
    long TargetMilliseconds,
    long ObservedMilliseconds,
    int Width,
    int Height,
    int Stride,
    byte[] BgraPixels);

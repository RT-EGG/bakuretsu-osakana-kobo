namespace BakuretsuOsakanaKobo.Playback;

public static class PlaybackVolume
{
    public const int DefaultPercent = 100;

    public const int BasicMaximumPercent = 100;

    public const int MaximumPercent = 500;

    public const int WheelStepPercent = 5;

    public static int ClampBasic(int volumePercent) =>
        Math.Clamp(volumePercent, 0, BasicMaximumPercent);

    public static int Clamp(int volumePercent) =>
        Math.Clamp(volumePercent, 0, MaximumPercent);
}

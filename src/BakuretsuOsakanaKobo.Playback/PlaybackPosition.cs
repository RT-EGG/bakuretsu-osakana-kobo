namespace BakuretsuOsakanaKobo.Playback;

public static class PlaybackPosition
{
    public static double Normalize(double normalizedPosition) =>
        double.IsNaN(normalizedPosition) ? 0 : Math.Clamp(normalizedPosition, 0d, 1d);
}

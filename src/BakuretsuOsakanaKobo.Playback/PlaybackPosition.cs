namespace BakuretsuOsakanaKobo.Playback;

public static class PlaybackPosition
{
    public static double Normalize(double normalizedPosition) =>
        double.IsNaN(normalizedPosition) ? 0 : Math.Clamp(normalizedPosition, 0d, 1d);

    public static double OffsetByMilliseconds(
        long currentMilliseconds,
        long lengthMilliseconds,
        long offsetMilliseconds)
    {
        if (lengthMilliseconds <= 0)
        {
            return 0;
        }

        var targetMilliseconds = Math.Clamp(
            (decimal)currentMilliseconds + offsetMilliseconds,
            0,
            lengthMilliseconds);
        return (double)(targetMilliseconds / lengthMilliseconds);
    }
}

namespace BakuretsuOsakanaKobo;

internal static class SeekUiGeometry
{
    public static double MarkerOffset(
        double positionMilliseconds,
        double durationMilliseconds,
        double trackWidth,
        double markerWidth)
    {
        if (durationMilliseconds <= 0 || trackWidth <= 0 || markerWidth <= 0)
        {
            return 0;
        }

        var center = Math.Clamp(positionMilliseconds / durationMilliseconds, 0, 1) * trackWidth;
        return Math.Clamp(center - (markerWidth / 2), 0, Math.Max(0, trackWidth - markerWidth));
    }
}

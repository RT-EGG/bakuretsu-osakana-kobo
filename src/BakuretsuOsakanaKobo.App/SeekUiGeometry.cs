namespace BakuretsuOsakanaKobo;

internal static class SeekUiGeometry
{
    public static double PositionFromPointer(
        double pointerX,
        double trackWidth,
        double durationMilliseconds)
    {
        if (trackWidth <= 0 || durationMilliseconds <= 0)
        {
            return 0;
        }

        return Math.Clamp(pointerX / trackWidth, 0, 1) * durationMilliseconds;
    }

    public static double PopupOffset(double pointerX, double trackWidth, double popupWidth)
    {
        if (trackWidth <= 0 || popupWidth <= 0)
        {
            return 0;
        }

        return Math.Clamp(pointerX - (popupWidth / 2), 0, Math.Max(0, trackWidth - popupWidth));
    }

    public static long ThumbnailTargetMilliseconds(
        double positionMilliseconds,
        double durationMilliseconds,
        double intervalPercent)
    {
        if (durationMilliseconds <= 0 || intervalPercent <= 0)
        {
            return 0;
        }

        var percent = Math.Clamp(positionMilliseconds / durationMilliseconds, 0, 1) * 100;
        var maximumSlot = (int)Math.Ceiling(100 / intervalPercent);
        var slot = Math.Clamp((int)Math.Round(percent / intervalPercent), 0, maximumSlot);
        var target = Math.Round(durationMilliseconds * Math.Min(100, slot * intervalPercent) / 100);
        return Math.Clamp((long)target, 0, Math.Max(0, (long)durationMilliseconds - 1));
    }

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

namespace BakuretsuOsakanaKobo.Phase3UiMock;

internal static class SeekUiGeometry
{
    public static double PositionFromPointer(double pointerX, double trackWidth, double durationSeconds)
    {
        if (trackWidth <= 0 || durationSeconds <= 0)
        {
            return 0;
        }

        return Math.Clamp(pointerX / trackWidth, 0, 1) * durationSeconds;
    }

    public static double PopupOffset(double pointerX, double trackWidth, double popupWidth)
    {
        if (trackWidth <= 0 || popupWidth <= 0)
        {
            return 0;
        }

        return Math.Clamp(pointerX - (popupWidth / 2), 0, Math.Max(0, trackWidth - popupWidth));
    }

    public static double MarkerOffset(double positionSeconds, double durationSeconds, double trackWidth, double markerWidth)
    {
        if (durationSeconds <= 0 || trackWidth <= 0 || markerWidth <= 0)
        {
            return 0;
        }

        var center = Math.Clamp(positionSeconds / durationSeconds, 0, 1) * trackWidth;
        return Math.Clamp(center - (markerWidth / 2), 0, Math.Max(0, trackWidth - markerWidth));
    }

    public static int ThumbnailSlot(double positionSeconds, double durationSeconds, double intervalPercent)
    {
        if (durationSeconds <= 0 || intervalPercent <= 0)
        {
            return 0;
        }

        var percent = Math.Clamp(positionSeconds / durationSeconds, 0, 1) * 100;
        var maximumSlot = (int)Math.Ceiling(100 / intervalPercent);
        return Math.Clamp((int)Math.Round(percent / intervalPercent), 0, maximumSlot);
    }

    public static int ThumbnailSlotCount(double intervalPercent) => intervalPercent <= 0
        ? 0
        : (int)Math.Ceiling(100 / intervalPercent) + 1;
}

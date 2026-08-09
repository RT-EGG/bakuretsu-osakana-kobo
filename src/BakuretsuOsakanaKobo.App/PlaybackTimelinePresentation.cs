namespace BakuretsuOsakanaKobo;

public sealed record PlaybackTimelinePresentation(
    bool IsSeekEnabled,
    double NormalizedPosition,
    string TimeText,
    string SeekToolTip)
{
    public const string UnknownTimeText = "--:--:-- / --:--:--";

    public static PlaybackTimelinePresentation From(
        bool hasMedia,
        bool isLoading,
        bool isSeekable,
        long timeMilliseconds,
        long lengthMilliseconds)
    {
        if (!hasMedia)
        {
            return Unavailable("動画を開くとシークできます");
        }

        if (isLoading)
        {
            return Unavailable("動画を読み込んでいます");
        }

        if (lengthMilliseconds <= 0)
        {
            return Unavailable("動画の長さを取得できないためシークできません");
        }

        var clampedTime = Math.Clamp(timeMilliseconds, 0, lengthMilliseconds);
        return new PlaybackTimelinePresentation(
            isSeekable,
            (double)clampedTime / lengthMilliseconds,
            $"{FormatMilliseconds(clampedTime)} / {FormatMilliseconds(lengthMilliseconds)}",
            isSeekable ? "再生位置" : "この動画はシークできません");
    }

    public static string FormatMilliseconds(long milliseconds)
    {
        var totalSeconds = Math.Max(0, milliseconds) / 1_000;
        var hours = totalSeconds / 3_600;
        var minutes = totalSeconds % 3_600 / 60;
        var seconds = totalSeconds % 60;
        return $"{hours:D2}:{minutes:D2}:{seconds:D2}";
    }

    private static PlaybackTimelinePresentation Unavailable(string toolTip) =>
        new(false, 0, UnknownTimeText, toolTip);
}

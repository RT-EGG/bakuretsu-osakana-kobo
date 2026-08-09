namespace BakuretsuOsakanaKobo;

public sealed record PlaybackButtonPresentation(
    bool IsEnabled,
    string Glyph,
    string AccessibleName,
    string ToolTip)
{
    public static PlaybackButtonPresentation From(bool hasMedia, bool isPlaying)
    {
        if (!hasMedia)
        {
            return new PlaybackButtonPresentation(
                false,
                "▶",
                "再生",
                "動画を開くと再生できます");
        }

        return isPlaying
            ? new PlaybackButtonPresentation(true, "Ⅱ", "一時停止", "一時停止")
            : new PlaybackButtonPresentation(true, "▶", "再生", "再生");
    }
}

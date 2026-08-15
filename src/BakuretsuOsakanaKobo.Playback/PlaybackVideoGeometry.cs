namespace BakuretsuOsakanaKobo.Playback;

public static class PlaybackVideoGeometry
{
    public const double DefaultDisplayAspectRatio = 16.0 / 9.0;

    public static double DisplayAspectRatio(
        uint width,
        uint height,
        uint sampleAspectRatioNumerator,
        uint sampleAspectRatioDenominator,
        bool swapsAxes)
    {
        if (width == 0 || height == 0)
        {
            return DefaultDisplayAspectRatio;
        }

        var pixelAspectRatio = sampleAspectRatioNumerator > 0 && sampleAspectRatioDenominator > 0
            ? sampleAspectRatioNumerator / (double)sampleAspectRatioDenominator
            : 1;
        var aspectRatio = width * pixelAspectRatio / height;
        if (!double.IsFinite(aspectRatio) || aspectRatio <= 0)
        {
            return DefaultDisplayAspectRatio;
        }

        return swapsAxes ? 1 / aspectRatio : aspectRatio;
    }
}

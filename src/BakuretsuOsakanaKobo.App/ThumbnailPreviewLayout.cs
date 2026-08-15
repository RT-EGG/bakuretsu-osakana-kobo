using BakuretsuOsakanaKobo.Infrastructure.Persistence;
using BakuretsuOsakanaKobo.Playback;

namespace BakuretsuOsakanaKobo;

internal readonly record struct ThumbnailPreviewLayout(
    double ImageWidth,
    double ImageHeight,
    double PopupWidth,
    double PopupHeight)
{
    private const double PopupChromeWidth = 12;
    private const double PopupChromeHeight = 12;
    private const double TimeRowHeight = 28;

    public static ThumbnailPreviewLayout Create(
        double windowWidth,
        double widthPercent,
        double displayAspectRatio)
    {
        var imageWidth = ThumbnailPreviewSize.ResolveWidth(windowWidth, widthPercent);
        var resolvedAspectRatio = double.IsFinite(displayAspectRatio) && displayAspectRatio > 0
            ? displayAspectRatio
            : PlaybackVideoGeometry.DefaultDisplayAspectRatio;
        var imageHeight = imageWidth / resolvedAspectRatio;
        return new ThumbnailPreviewLayout(
            imageWidth,
            imageHeight,
            imageWidth + PopupChromeWidth,
            imageHeight + TimeRowHeight + PopupChromeHeight);
    }
}

using BakuretsuOsakanaKobo.Infrastructure.Persistence;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class SeekUiGeometryTests
{
    [Theory]
    [InlineData(720, 5, 60)]
    [InlineData(1200, 5, 60)]
    [InlineData(1000, 10, 100)]
    [InlineData(1000, 15, 150)]
    [InlineData(2000, 30, 320)]
    public void PreviewWidthUsesWindowPercentageAndPixelLimits(
        double windowWidth,
        double percent,
        double expected)
    {
        Assert.Equal(expected, ThumbnailPreviewSize.ResolveWidth(windowWidth, percent));
    }

    [Theory]
    [InlineData(1000, 15, 16.0 / 9.0, 150, 84.375)]
    [InlineData(1000, 15, 9.0 / 16.0, 150, 266.6666666666667)]
    public void PreviewLayoutFollowsVideoAspectRatio(
        double windowWidth,
        double percent,
        double aspectRatio,
        double expectedWidth,
        double expectedHeight)
    {
        var layout = ThumbnailPreviewLayout.Create(windowWidth, percent, aspectRatio);

        Assert.Equal(expectedWidth, layout.ImageWidth);
        Assert.Equal(expectedHeight, layout.ImageHeight, precision: 10);
        Assert.Equal(expectedWidth + 12, layout.PopupWidth);
        Assert.Equal(expectedHeight + 40, layout.PopupHeight, precision: 10);
    }

    [Theory]
    [InlineData(0, 500, 240, 0)]
    [InlineData(250, 500, 240, 130)]
    [InlineData(500, 500, 240, 260)]
    public void PopupOffsetCentersAndClampsPopup(
        double pointerX,
        double trackWidth,
        double popupWidth,
        double expected)
    {
        Assert.Equal(expected, SeekUiGeometry.PopupOffset(pointerX, trackWidth, popupWidth));
    }

    [Theory]
    [InlineData(0, 500, 60_000, 0)]
    [InlineData(250, 500, 60_000, 30_000)]
    [InlineData(600, 500, 60_000, 60_000)]
    public void PositionFromPointerClampsToDuration(
        double pointerX,
        double trackWidth,
        double duration,
        double expected)
    {
        Assert.Equal(expected, SeekUiGeometry.PositionFromPointer(pointerX, trackWidth, duration));
    }

    [Theory]
    [InlineData(29_800, 60_000, 1, 30_000)]
    [InlineData(750, 60_000, 0.25, 750)]
    [InlineData(60_000, 60_000, 1, 59_999)]
    public void ThumbnailTargetMapsToConfiguredSlot(
        double position,
        double duration,
        double intervalPercent,
        long expected)
    {
        Assert.Equal(
            expected,
            SeekUiGeometry.ThumbnailTargetMilliseconds(position, duration, intervalPercent));
    }

    [Theory]
    [InlineData(0, 100, 400, 8, 0)]
    [InlineData(50, 100, 400, 8, 196)]
    [InlineData(100, 100, 400, 8, 392)]
    [InlineData(-10, 100, 400, 8, 0)]
    [InlineData(110, 100, 400, 8, 392)]
    public void MarkerOffsetCentersAndClampsMarker(
        double position,
        double duration,
        double trackWidth,
        double markerWidth,
        double expected)
    {
        Assert.Equal(
            expected,
            SeekUiGeometry.MarkerOffset(position, duration, trackWidth, markerWidth));
    }

    [Theory]
    [InlineData(0, 400, 8)]
    [InlineData(100, 0, 8)]
    [InlineData(100, 400, 0)]
    public void MarkerOffsetReturnsZeroForUnavailableGeometry(
        double duration,
        double trackWidth,
        double markerWidth)
    {
        Assert.Equal(0, SeekUiGeometry.MarkerOffset(50, duration, trackWidth, markerWidth));
    }
}

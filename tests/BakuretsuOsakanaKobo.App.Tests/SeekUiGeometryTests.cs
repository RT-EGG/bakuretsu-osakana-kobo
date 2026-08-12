using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class SeekUiGeometryTests
{
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

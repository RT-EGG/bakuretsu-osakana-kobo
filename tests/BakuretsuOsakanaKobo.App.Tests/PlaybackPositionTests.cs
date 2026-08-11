using BakuretsuOsakanaKobo.Playback;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class PlaybackPositionTests
{
    [Theory]
    [InlineData(double.NegativeInfinity, 0)]
    [InlineData(-0.1, 0)]
    [InlineData(0, 0)]
    [InlineData(0.5, 0.5)]
    [InlineData(1, 1)]
    [InlineData(1.1, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    public void Normalize_ClampsToMediaRange(double requestedPosition, double expectedPosition)
    {
        Assert.Equal(expectedPosition, PlaybackPosition.Normalize(requestedPosition));
    }

    [Fact]
    public void Normalize_MapsNaNToBeginning()
    {
        Assert.Equal(0, PlaybackPosition.Normalize(double.NaN));
    }

    [Theory]
    [InlineData(30_000, 60_000, -5_000, 25_000d / 60_000d)]
    [InlineData(30_000, 60_000, 5_000, 35_000d / 60_000d)]
    [InlineData(2_000, 60_000, -5_000, 0)]
    [InlineData(58_000, 60_000, 5_000, 1)]
    [InlineData(-1_000, 60_000, 5_000, 4_000d / 60_000d)]
    public void OffsetByMilliseconds_ClampsToMediaRange(
        long currentMilliseconds,
        long lengthMilliseconds,
        long offsetMilliseconds,
        double expected)
    {
        Assert.Equal(
            expected,
            PlaybackPosition.OffsetByMilliseconds(currentMilliseconds, lengthMilliseconds, offsetMilliseconds),
            precision: 10);
    }

    [Fact]
    public void OffsetByMilliseconds_WithUnknownLength_ReturnsBeginning()
    {
        Assert.Equal(0, PlaybackPosition.OffsetByMilliseconds(1_000, 0, 5_000));
    }
}

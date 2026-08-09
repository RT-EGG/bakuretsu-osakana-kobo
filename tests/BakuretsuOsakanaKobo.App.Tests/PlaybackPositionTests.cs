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
}

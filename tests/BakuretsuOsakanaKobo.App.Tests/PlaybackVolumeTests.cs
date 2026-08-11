using BakuretsuOsakanaKobo.Playback;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class PlaybackVolumeTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(65, 65)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    public void ClampBasic_RestrictsVolumeToNativeRange(int value, int expected)
    {
        Assert.Equal(expected, PlaybackVolume.ClampBasic(value));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(100, 100)]
    [InlineData(500, 500)]
    [InlineData(501, 500)]
    public void Clamp_RestrictsVolumeToProductRange(int value, int expected)
    {
        Assert.Equal(expected, PlaybackVolume.Clamp(value));
    }
}

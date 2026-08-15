using BakuretsuOsakanaKobo.Playback;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class PlaybackVideoGeometryTests
{
    [Theory]
    [InlineData(1920, 1080, 1, 1, false, 16.0 / 9.0)]
    [InlineData(720, 576, 16, 15, false, 4.0 / 3.0)]
    [InlineData(1920, 1080, 1, 1, true, 9.0 / 16.0)]
    public void DisplayAspectRatioIncludesSampleAspectRatioAndRotation(
        uint width,
        uint height,
        uint sarNumerator,
        uint sarDenominator,
        bool swapsAxes,
        double expected)
    {
        Assert.Equal(
            expected,
            PlaybackVideoGeometry.DisplayAspectRatio(
                width,
                height,
                sarNumerator,
                sarDenominator,
                swapsAxes),
            precision: 10);
    }

    [Fact]
    public void DisplayAspectRatioUsesFallbackForMissingDimensions()
    {
        Assert.Equal(
            PlaybackVideoGeometry.DefaultDisplayAspectRatio,
            PlaybackVideoGeometry.DisplayAspectRatio(0, 0, 0, 0, swapsAxes: false));
    }
}

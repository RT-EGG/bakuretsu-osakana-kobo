using BakuretsuOsakanaKobo.Playback;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class PlaybackRateTests
{
    [Theory]
    [InlineData(0.25f, true)]
    [InlineData(0.5f, true)]
    [InlineData(1.0f, true)]
    [InlineData(1.5f, true)]
    [InlineData(2.0f, true)]
    [InlineData(0.0f, false)]
    [InlineData(0.75f, false)]
    [InlineData(2.01f, false)]
    public void IsSupported_AcceptsOnlyDefinedRates(float rate, bool expected)
    {
        Assert.Equal(expected, PlaybackRate.IsSupported(rate));
    }

    [Theory]
    [InlineData("0.25", 0.25f)]
    [InlineData("0.5", 0.5f)]
    [InlineData("1.0", 1.0f)]
    [InlineData("1.5", 1.5f)]
    [InlineData("2.0", 2.0f)]
    public void TryParse_AcceptsInvariantDefinedRates(string text, float expected)
    {
        Assert.True(PlaybackRate.TryParse(text, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("0,5")]
    [InlineData("3.0")]
    public void TryParse_RejectsUndefinedOrCultureSpecificValues(string? text)
    {
        Assert.False(PlaybackRate.TryParse(text, out var actual));
        Assert.Equal(PlaybackRate.Default, actual);
    }

    [Theory]
    [InlineData(0.25f, "0.25×")]
    [InlineData(0.5f, "0.5×")]
    [InlineData(1.0f, "1.0×")]
    [InlineData(1.5f, "1.5×")]
    [InlineData(2.0f, "2.0×")]
    [InlineData(float.NaN, "1.0×")]
    public void Format_UsesApprovedDisplay(float rate, string expected)
    {
        Assert.Equal(expected, PlaybackRate.Format(rate));
    }

    [Theory]
    [InlineData(0.25f, -1, 0.25f)]
    [InlineData(0.25f, 1, 0.5f)]
    [InlineData(1.0f, -1, 0.5f)]
    [InlineData(1.0f, 1, 1.5f)]
    [InlineData(2.0f, 1, 2.0f)]
    [InlineData(1.5f, 0, 1.5f)]
    [InlineData(0.75f, 1, 1.0f)]
    public void Step_MovesOneDefinedRateAndClamps(float current, int direction, float expected)
    {
        Assert.Equal(expected, PlaybackRate.Step(current, direction));
    }
}

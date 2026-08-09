using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class PlaybackTimelinePresentationTests
{
    [Fact]
    public void From_WithoutMedia_ShowsUnknownTimeAndDisablesSeek()
    {
        var presentation = PlaybackTimelinePresentation.From(
            hasMedia: false,
            isLoading: false,
            isSeekable: false,
            timeMilliseconds: 0,
            lengthMilliseconds: 0);

        Assert.False(presentation.IsSeekEnabled);
        Assert.Equal(0, presentation.NormalizedPosition);
        Assert.Equal(PlaybackTimelinePresentation.UnknownTimeText, presentation.TimeText);
    }

    [Fact]
    public void From_WhileLoading_HidesExistingMediaTime()
    {
        var presentation = PlaybackTimelinePresentation.From(
            hasMedia: true,
            isLoading: true,
            isSeekable: true,
            timeMilliseconds: 30_000,
            lengthMilliseconds: 60_000);

        Assert.False(presentation.IsSeekEnabled);
        Assert.Equal(PlaybackTimelinePresentation.UnknownTimeText, presentation.TimeText);
    }

    [Fact]
    public void From_WithUnknownLength_DisablesSeek()
    {
        var presentation = PlaybackTimelinePresentation.From(
            hasMedia: true,
            isLoading: false,
            isSeekable: true,
            timeMilliseconds: 1_000,
            lengthMilliseconds: 0);

        Assert.False(presentation.IsSeekEnabled);
        Assert.Equal(PlaybackTimelinePresentation.UnknownTimeText, presentation.TimeText);
    }

    [Fact]
    public void From_WithKnownNonSeekableMedia_ShowsTimeButDisablesSeek()
    {
        var presentation = PlaybackTimelinePresentation.From(
            hasMedia: true,
            isLoading: false,
            isSeekable: false,
            timeMilliseconds: 65_000,
            lengthMilliseconds: 125_000);

        Assert.False(presentation.IsSeekEnabled);
        Assert.Equal("00:01:05 / 00:02:05", presentation.TimeText);
    }

    [Theory]
    [InlineData(-1, 10_000, 0, "00:00:00 / 00:00:10")]
    [InlineData(5_000, 10_000, 0.5, "00:00:05 / 00:00:10")]
    [InlineData(12_000, 10_000, 1, "00:00:10 / 00:00:10")]
    [InlineData(90_061_000, 100_000_000, 0.90061, "25:01:01 / 27:46:40")]
    public void From_ClampsPositionAndFormatsZeroPaddedTime(
        long timeMilliseconds,
        long lengthMilliseconds,
        double expectedPosition,
        string expectedTimeText)
    {
        var presentation = PlaybackTimelinePresentation.From(
            hasMedia: true,
            isLoading: false,
            isSeekable: true,
            timeMilliseconds,
            lengthMilliseconds);

        Assert.True(presentation.IsSeekEnabled);
        Assert.Equal(expectedPosition, presentation.NormalizedPosition, 5);
        Assert.Equal(expectedTimeText, presentation.TimeText);
    }
}

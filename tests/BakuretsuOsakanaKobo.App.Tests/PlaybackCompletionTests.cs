using BakuretsuOsakanaKobo.Playback;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class PlaybackCompletionTests
{
    [Theory]
    [InlineData(1_770, 5_000, true)]
    [InlineData(3_999, 5_000, true)]
    [InlineData(4_000, 5_000, false)]
    [InlineData(58_799, 60_000, true)]
    [InlineData(58_800, 60_000, false)]
    [InlineData(7_194_999, 7_200_000, true)]
    [InlineData(7_195_000, 7_200_000, false)]
    public void IsPrematureEnd_UsesBoundedAbsoluteAndProportionalTolerance(
        long playbackTimeMilliseconds,
        long lengthMilliseconds,
        bool expected)
    {
        Assert.Equal(
            expected,
            PlaybackCompletion.IsPrematureEnd(
                playbackTimeMilliseconds,
                lengthMilliseconds));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1_999)]
    [InlineData(-1, 1_000)]
    public void IsPrematureEnd_DoesNotClassifyVeryShortOrUnknownMedia(
        long playbackTimeMilliseconds,
        long lengthMilliseconds)
    {
        Assert.False(PlaybackCompletion.IsPrematureEnd(
            playbackTimeMilliseconds,
            lengthMilliseconds));
    }

    [Theory]
    [InlineData(4_900, 5_000, 5_000)]
    [InlineData(4_900, 0, 4_900)]
    [InlineData(-1, 0, 0)]
    public void TerminalTime_PresentsKnownNaturalEndAtTheMediaLength(
        long playbackTime,
        long length,
        long expected)
    {
        Assert.Equal(expected, PlaybackCompletion.TerminalTime(playbackTime, length));
    }

    [Theory]
    [InlineData(false, 0.4, 0)]
    [InlineData(true, 0.4, 0.4)]
    [InlineData(true, -1, 0)]
    [InlineData(true, 2, 1)]
    public void ReplayPosition_UsesTheBeginningUntilTheUserSeeksAfterCompletion(
        bool positionChanged,
        double selectedPosition,
        double expected)
    {
        Assert.Equal(
            expected,
            PlaybackCompletion.ReplayPosition(positionChanged, selectedPosition),
            precision: 6);
    }

    [Theory]
    [InlineData(0.4, 5_000, 2_000)]
    [InlineData(2, 5_000, 5_000)]
    [InlineData(0.4, 0, 0)]
    public void TimeAtPosition_ClampsAndConvertsTheCompletedSeekPosition(
        double position,
        long length,
        long expected)
    {
        Assert.Equal(expected, PlaybackCompletion.TimeAtPosition(position, length));
    }
}

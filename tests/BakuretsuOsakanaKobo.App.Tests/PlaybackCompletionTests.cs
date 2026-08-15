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
}

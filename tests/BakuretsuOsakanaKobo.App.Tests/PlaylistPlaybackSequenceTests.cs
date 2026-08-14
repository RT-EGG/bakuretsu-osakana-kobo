using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class PlaylistPlaybackSequenceTests
{
    [Fact]
    public void GetExistingCandidateIndices_StartsAtRequestedRegistrationAndSkipsMissingEntries()
    {
        var entries = new[] { "first.mp4", "missing.wmv", "last.mp4", "first.mp4" };

        var candidates = PlaylistPlaybackSequence.GetExistingCandidateIndices(
            entries,
            startIndex: 1,
            wrapToStart: false,
            path => path != "missing.wmv");

        Assert.Equal([2, 3], candidates);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void GetExistingCandidateIndices_AllowsFirstAndEndBoundaries(int startIndex)
    {
        var entries = new[] { "a.mp4", "b.mp4", "c.mp4" };

        var candidates = PlaylistPlaybackSequence.GetExistingCandidateIndices(
            entries,
            startIndex,
            wrapToStart: false,
            _ => true);

        Assert.Equal(Enumerable.Range(startIndex, entries.Length - startIndex), candidates);
    }

    [Fact]
    public void GetExistingCandidateIndices_WrapsOnceAndSkipsMissingEntries()
    {
        var entries = new[] { "first.mp4", "missing.wmv", "last.mp4" };

        var candidates = PlaylistPlaybackSequence.GetExistingCandidateIndices(
            entries,
            startIndex: 3,
            wrapToStart: true,
            path => path != "missing.wmv");

        Assert.Equal([0, 2], candidates);
    }

    [Fact]
    public void GetExistingCandidateIndices_DoesNotRepeatCandidatesWhenWrapping()
    {
        var entries = new[] { "first.mp4", "middle.mp4", "last.mp4" };

        var candidates = PlaylistPlaybackSequence.GetExistingCandidateIndices(
            entries,
            startIndex: 2,
            wrapToStart: true,
            _ => true);

        Assert.Equal([2, 0, 1], candidates);
    }

    [Fact]
    public void RemapAfterRemoval_KeepsRegistrationIdentityOrClearsRemovedCurrent()
    {
        Assert.Equal(2, PlaylistPlaybackSequence.RemapAfterRemoval(4, [1, 3]));
        Assert.Null(PlaylistPlaybackSequence.RemapAfterRemoval(3, [1, 3]));
        Assert.Null(PlaylistPlaybackSequence.RemapAfterRemoval(null, [1]));
    }

    [Theory]
    [InlineData(0, 0, 4, 3)]
    [InlineData(2, 0, 4, 1)]
    [InlineData(3, 3, 0, 0)]
    [InlineData(1, 3, 0, 2)]
    public void RemapAfterMove_KeepsRegistrationIdentity(
        int index,
        int sourceIndex,
        int insertionIndex,
        int expected)
    {
        Assert.Equal(
            expected,
            PlaylistPlaybackSequence.RemapAfterMove(index, sourceIndex, insertionIndex));
    }
}

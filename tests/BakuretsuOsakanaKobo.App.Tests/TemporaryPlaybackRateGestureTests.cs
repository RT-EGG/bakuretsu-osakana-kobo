using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class TemporaryPlaybackRateGestureTests
{
    [Fact]
    public void HoldDuration_IsApprovedFourHundredMilliseconds()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(400), TemporaryPlaybackRateGesture.HoldDuration);
    }

    [Fact]
    public void Hold_ActivatesAndRestoresRateCapturedAtActivation()
    {
        var gesture = new TemporaryPlaybackRateGesture();

        gesture.Begin(100, 200);
        Assert.False(gesture.CancelIfMoved(104, 204, 4, 4));
        Assert.True(gesture.TryActivate(canActivate: true, currentRate: 0.5f));

        Assert.False(gesture.IsPending);
        Assert.True(gesture.IsActive);
        Assert.Equal(0.5f, gesture.End());
        Assert.False(gesture.IsActive);
    }

    [Fact]
    public void MovementBeyondDragThreshold_CancelsBeforeActivation()
    {
        var gesture = new TemporaryPlaybackRateGesture();

        gesture.Begin(10, 20);

        Assert.True(gesture.CancelIfMoved(15, 20, 4, 4));
        Assert.False(gesture.TryActivate(canActivate: true, currentRate: 1.0f));
        Assert.Null(gesture.End());
    }

    [Fact]
    public void MovementAfterActivation_DoesNotCancelTemporaryRate()
    {
        var gesture = new TemporaryPlaybackRateGesture();
        gesture.Begin(10, 20);
        Assert.True(gesture.TryActivate(canActivate: true, currentRate: 1.5f));

        Assert.False(gesture.CancelIfMoved(100, 200, 4, 4));

        Assert.True(gesture.IsActive);
        Assert.Equal(1.5f, gesture.End());
    }

    [Fact]
    public void ReleaseBeforeActivation_IsAPlainClickWithoutRateRestore()
    {
        var gesture = new TemporaryPlaybackRateGesture();

        gesture.Begin(10, 20);

        Assert.Null(gesture.End());
        Assert.False(gesture.IsPending);
        Assert.False(gesture.IsActive);
    }

    [Fact]
    public void ActivationWithoutControllablePlayback_CancelsGesture()
    {
        var gesture = new TemporaryPlaybackRateGesture();
        gesture.Begin(10, 20);

        Assert.False(gesture.TryActivate(canActivate: false, currentRate: 0.25f));

        Assert.False(gesture.IsPending);
        Assert.False(gesture.IsActive);
        Assert.Null(gesture.End());
    }
}

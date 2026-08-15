using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class FullscreenControlsStateTests
{
    [Fact]
    public void AutoHideDelay_IsApprovedThreeSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), FullscreenControlsState.AutoHideDelay);
    }

    [Fact]
    public void Timeout_HidesOnlyWhileFullscreenAndIdle()
    {
        var state = new FullscreenControlsState();

        Assert.False(state.TryHideAfterTimeout());
        state.EnterFullscreen();
        Assert.True(state.AreControlsVisible);
        Assert.True(state.TryHideAfterTimeout());
        Assert.False(state.AreControlsVisible);

        Assert.True(state.ShowForActivity());
        Assert.True(state.AreControlsVisible);
        state.ExitFullscreen();
        Assert.False(state.TryHideAfterTimeout());
        Assert.True(state.AreControlsVisible);
    }

    [Fact]
    public void PointerOverControls_PreventsHidingUntilPointerLeaves()
    {
        var state = new FullscreenControlsState();
        state.EnterFullscreen();

        Assert.True(state.SetPointerOverControls(true));
        Assert.True(state.IsInteractionActive);
        Assert.False(state.TryHideAfterTimeout());

        Assert.True(state.SetPointerOverControls(false));
        Assert.False(state.IsInteractionActive);
        Assert.True(state.TryHideAfterTimeout());
    }

    [Fact]
    public void ContextMenuAndPointerPins_AreIndependent()
    {
        var state = new FullscreenControlsState();
        state.EnterFullscreen();
        state.SetPointerOverControls(true);
        state.SetContextMenuOpen(true);

        state.SetPointerOverControls(false);
        Assert.True(state.IsInteractionActive);
        Assert.False(state.TryHideAfterTimeout());

        state.SetContextMenuOpen(false);
        Assert.False(state.IsInteractionActive);
        Assert.True(state.TryHideAfterTimeout());
    }
}

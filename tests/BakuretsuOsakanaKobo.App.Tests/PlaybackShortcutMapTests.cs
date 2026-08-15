using System.Windows.Input;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class PlaybackShortcutMapTests
{
    [Fact]
    public void SeekStep_IsApprovedFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), PlaybackShortcutMap.SeekStep);
    }

    [Fact]
    public void UnmodifiedPlaybackKeys_MapToApprovedActions()
    {
        var expectations = new[]
        {
            (Key.Space, PlaybackShortcutAction.TogglePlayPause),
            (Key.Left, PlaybackShortcutAction.SeekBackward),
            (Key.Right, PlaybackShortcutAction.SeekForward),
            (Key.Up, PlaybackShortcutAction.IncreasePlaybackRate),
            (Key.Down, PlaybackShortcutAction.DecreasePlaybackRate),
        };

        foreach (var (key, expected) in expectations)
        {
            Assert.Equal(expected, PlaybackShortcutMap.Resolve(key, ModifierKeys.None, isFullscreen: false));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AltEnter_AlwaysTogglesFullscreen(bool isFullscreen)
    {
        Assert.Equal(
            PlaybackShortcutAction.ToggleFullscreen,
            PlaybackShortcutMap.Resolve(Key.Enter, ModifierKeys.Alt, isFullscreen));
    }

    [Theory]
    [InlineData(ModifierKeys.None)]
    [InlineData(ModifierKeys.Control)]
    public void Escape_ExitsOnlyWhileFullscreen(ModifierKeys modifiers)
    {
        Assert.Equal(
            PlaybackShortcutAction.ExitFullscreen,
            PlaybackShortcutMap.Resolve(Key.Escape, modifiers, isFullscreen: true));
        Assert.Equal(
            PlaybackShortcutAction.None,
            PlaybackShortcutMap.Resolve(Key.Escape, modifiers, isFullscreen: false));
    }

    [Theory]
    [InlineData(Key.Enter, ModifierKeys.None)]
    [InlineData(Key.Enter, ModifierKeys.Control)]
    [InlineData(Key.Space, ModifierKeys.Control)]
    [InlineData(Key.Left, ModifierKeys.Alt)]
    [InlineData(Key.Up, ModifierKeys.Shift)]
    public void UnassignedOrModifiedCombination_DoesNothing(Key key, ModifierKeys modifiers)
    {
        Assert.Equal(
            PlaybackShortcutAction.None,
            PlaybackShortcutMap.Resolve(key, modifiers, isFullscreen: true));
    }
}

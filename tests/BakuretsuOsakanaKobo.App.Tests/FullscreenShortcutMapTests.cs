using System.Windows.Input;
using Xunit;

namespace BakuretsuOsakanaKobo.App.Tests;

public sealed class FullscreenShortcutMapTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AltEnter_AlwaysTogglesFullscreen(bool isFullscreen)
    {
        Assert.Equal(
            FullscreenShortcutAction.Toggle,
            FullscreenShortcutMap.Resolve(Key.Enter, ModifierKeys.Alt, isFullscreen));
    }

    [Fact]
    public void Escape_ExitsOnlyWhileFullscreen()
    {
        Assert.Equal(
            FullscreenShortcutAction.Exit,
            FullscreenShortcutMap.Resolve(Key.Escape, ModifierKeys.None, isFullscreen: true));
        Assert.Equal(
            FullscreenShortcutAction.None,
            FullscreenShortcutMap.Resolve(Key.Escape, ModifierKeys.None, isFullscreen: false));
    }

    [Theory]
    [InlineData(Key.Enter, ModifierKeys.None)]
    [InlineData(Key.Enter, ModifierKeys.Control)]
    [InlineData(Key.Escape, ModifierKeys.Alt)]
    [InlineData(Key.Space, ModifierKeys.None)]
    public void UnassignedCombination_DoesNothing(Key key, ModifierKeys modifiers)
    {
        Assert.Equal(
            FullscreenShortcutAction.None,
            FullscreenShortcutMap.Resolve(key, modifiers, isFullscreen: true));
    }
}

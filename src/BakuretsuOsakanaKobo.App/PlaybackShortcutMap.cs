using System.Windows.Input;

namespace BakuretsuOsakanaKobo;

internal enum PlaybackShortcutAction
{
    None,
    TogglePlayPause,
    SeekBackward,
    SeekForward,
    IncreasePlaybackRate,
    DecreasePlaybackRate,
    ToggleFullscreen,
    ExitFullscreen,
}

internal static class PlaybackShortcutMap
{
    internal static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(5);

    internal static PlaybackShortcutAction Resolve(Key key, ModifierKeys modifiers, bool isFullscreen)
    {
        if (key == Key.Enter && modifiers == ModifierKeys.Alt)
        {
            return PlaybackShortcutAction.ToggleFullscreen;
        }

        if (key == Key.Escape && isFullscreen)
        {
            return PlaybackShortcutAction.ExitFullscreen;
        }

        if (modifiers != ModifierKeys.None)
        {
            return PlaybackShortcutAction.None;
        }

        return key switch
        {
            Key.Space => PlaybackShortcutAction.TogglePlayPause,
            Key.Left => PlaybackShortcutAction.SeekBackward,
            Key.Right => PlaybackShortcutAction.SeekForward,
            Key.Up => PlaybackShortcutAction.IncreasePlaybackRate,
            Key.Down => PlaybackShortcutAction.DecreasePlaybackRate,
            _ => PlaybackShortcutAction.None,
        };
    }
}

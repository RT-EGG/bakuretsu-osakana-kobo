using System.Windows.Input;

namespace BakuretsuOsakanaKobo;

internal enum FullscreenShortcutAction
{
    None,
    Toggle,
    Exit,
}

internal static class FullscreenShortcutMap
{
    public static FullscreenShortcutAction Resolve(Key key, ModifierKeys modifiers, bool isFullscreen)
    {
        if (key == Key.Enter && modifiers == ModifierKeys.Alt)
        {
            return FullscreenShortcutAction.Toggle;
        }

        if (key == Key.Escape && modifiers == ModifierKeys.None && isFullscreen)
        {
            return FullscreenShortcutAction.Exit;
        }

        return FullscreenShortcutAction.None;
    }
}

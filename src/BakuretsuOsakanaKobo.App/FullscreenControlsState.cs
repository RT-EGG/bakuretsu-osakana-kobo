namespace BakuretsuOsakanaKobo;

internal sealed class FullscreenControlsState
{
    internal static readonly TimeSpan AutoHideDelay = TimeSpan.FromSeconds(3);

    internal bool IsFullscreen { get; private set; }

    internal bool AreControlsVisible { get; private set; } = true;

    internal bool IsInteractionActive => IsPointerOverControls || IsContextMenuOpen;

    private bool IsPointerOverControls { get; set; }

    private bool IsContextMenuOpen { get; set; }

    internal void EnterFullscreen()
    {
        IsFullscreen = true;
        AreControlsVisible = true;
        IsPointerOverControls = false;
        IsContextMenuOpen = false;
    }

    internal void ExitFullscreen()
    {
        IsFullscreen = false;
        AreControlsVisible = true;
        IsPointerOverControls = false;
        IsContextMenuOpen = false;
    }

    internal bool ShowForActivity()
    {
        if (!IsFullscreen)
        {
            return false;
        }

        AreControlsVisible = true;
        return true;
    }

    internal bool SetPointerOverControls(bool isPointerOverControls)
    {
        IsPointerOverControls = IsFullscreen && isPointerOverControls;
        return ShowForActivity();
    }

    internal bool SetContextMenuOpen(bool isContextMenuOpen)
    {
        IsContextMenuOpen = IsFullscreen && isContextMenuOpen;
        return ShowForActivity();
    }

    internal bool TryHideAfterTimeout()
    {
        if (!IsFullscreen || IsInteractionActive)
        {
            return false;
        }

        AreControlsVisible = false;
        return true;
    }
}

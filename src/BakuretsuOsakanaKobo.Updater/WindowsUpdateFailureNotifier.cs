using System.Runtime.InteropServices;
using BakuretsuOsakanaKobo.Update;

namespace BakuretsuOsakanaKobo.Updater;

internal sealed class WindowsUpdateFailureNotifier : IUpdateFailureNotifier
{
    private const uint IconWarning = 0x00000030;
    private const uint SetForeground = 0x00010000;

    public void Show(string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _ = MessageBox(nint.Zero, message, "爆裂おさかな工房 更新", IconWarning | SetForeground);
    }

#pragma warning disable SYSLIB1054 // A simple UTF-16 Win32 notification avoids enabling unsafe code in the helper.
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint window, string text, string caption, uint type);
#pragma warning restore SYSLIB1054
}

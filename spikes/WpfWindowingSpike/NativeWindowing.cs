using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BakuretsuOsakanaKobo.Spikes.WpfWindowing;

internal static class NativeWindowing
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpShowWindow = 0x0040;

    internal static WindowSnapshot Snapshot(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        _ = GetWindowRect(handle, out var rect);
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        return new WindowSnapshot
        {
            Handle = handle.ToInt64(),
            Foreground = GetForegroundWindow() == handle,
            Dpi = GetDpiForWindow(handle),
            MonitorHandle = monitor.ToInt64(),
            Left = rect.Left,
            Top = rect.Top,
            Width = rect.Right - rect.Left,
            Height = rect.Bottom - rect.Top,
        };
    }

    internal static IReadOnlyList<MonitorSnapshot> GetMonitors()
    {
        var monitors = new List<MonitorSnapshot>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                monitors.Add(new MonitorSnapshot
                {
                    Handle = monitor.ToInt64(),
                    Primary = (info.Flags & 1) != 0,
                    Left = info.Monitor.Left,
                    Top = info.Monitor.Top,
                    Width = info.Monitor.Right - info.Monitor.Left,
                    Height = info.Monitor.Bottom - info.Monitor.Top,
                    WorkLeft = info.Work.Left,
                    WorkTop = info.Work.Top,
                    WorkWidth = info.Work.Right - info.Work.Left,
                    WorkHeight = info.Work.Bottom - info.Work.Top,
                });
            }

            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    internal static void MoveToMonitor(Window window, MonitorSnapshot monitor)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var snapshot = Snapshot(window);
        var left = monitor.WorkLeft + Math.Max(0, (monitor.WorkWidth - snapshot.Width) / 2);
        var top = monitor.WorkTop + Math.Max(0, (monitor.WorkHeight - snapshot.Height) / 2);
        if (!SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, SwpNoSize | SwpNoZOrder | SwpShowWindow))
        {
            throw new InvalidOperationException($"SetWindowPos failed: {Marshal.GetLastWin32Error()}.");
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        IntPtr deviceContext,
        IntPtr clipRect,
        MonitorEnumProcedure callback,
        IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    private delegate bool MonitorEnumProcedure(IntPtr monitor, IntPtr deviceContext, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect Work;
        internal uint Flags;
    }
}

internal sealed class MonitorSnapshot
{
    public long Handle { get; set; }
    public bool Primary { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int WorkLeft { get; set; }
    public int WorkTop { get; set; }
    public int WorkWidth { get; set; }
    public int WorkHeight { get; set; }
}

internal sealed class WindowSnapshot
{
    public long Handle { get; set; }
    public bool Foreground { get; set; }
    public uint Dpi { get; set; }
    public long MonitorHandle { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

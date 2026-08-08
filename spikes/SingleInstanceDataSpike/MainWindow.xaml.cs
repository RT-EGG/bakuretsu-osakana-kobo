using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BakuretsuOsakanaKobo.Spikes.SingleInstanceData;

public partial class MainWindow : Window
{
    private readonly EventRecorder _events;
    private int _activationCount;

    internal MainWindow(EventRecorder events)
    {
        InitializeComponent();
        _events = events;
        ProcessText.Text = $"Primary PID: {Environment.ProcessId}";
    }

    internal async Task HandleLaunchRequestAsync(LaunchRequest request)
    {
        string action = "";
        string displayedPath = "";
        int activationCount = 0;
        await Dispatcher.InvokeAsync(() =>
        {
            _activationCount++;
            action = request.FileArguments.Length switch
            {
                0 => "activate-only",
                1 => "open-one",
                _ => "ignore-multiple",
            };

            if (request.FileArguments.Length == 1)
            {
                PathText.Text = request.FileArguments[0];
                StatusText.Text = request.IsInitialLaunch
                    ? "起動引数を受理しました"
                    : "IPCでファイルを受理しました";
            }
            else if (request.FileArguments.Length >= 2)
            {
                StatusText.Text = "複数ファイル引数を無視し、現在状態を維持しました";
            }
            else
            {
                StatusText.Text = request.IsInitialLaunch ? "待機中" : "IPCで前面化しました";
            }

            BringToForeground();
            activationCount = _activationCount;
            displayedPath = PathText.Text;
        });

        // SetForegroundWindow can complete after the activation call returns.
        // Measure after WPF has processed the activation/render messages.
        await Task.Delay(100);
        var foregroundObservedAfterDelay = await Dispatcher.InvokeAsync(
            () => GetForegroundWindow() == new WindowInteropHelper(this).Handle);
        _events.Record("launch-request", new
        {
            request.SenderProcessId,
            request.FileArguments,
            request.IsInitialLaunch,
            action,
            foregroundObservedAfterDelay,
            activationCount,
            displayedPath,
            processId = Environment.ProcessId,
        });
    }

    private void BringToForeground()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        _ = SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}

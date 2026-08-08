using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace BakuretsuOsakanaKobo.Spikes.LibVlcWpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var processStartedAt = Process.GetCurrentProcess().StartTime;
        var performanceOptions = PerformanceValidationOptions.TryParse(e.Args);
        WriteValidationLog("OnStartup entered");
        Core.Initialize();
        WriteValidationLog("LibVLC Core initialized");
        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        double? processStartToLoadedMs = null;
        window.Loaded += (_, _) =>
        {
            processStartToLoadedMs = (DateTime.Now - processStartedAt).TotalMilliseconds;
            WriteValidationLog(
                $"Loaded: visible={window.IsVisible}, handle={new WindowInteropHelper(window).Handle}");
        };
        window.Show();
        WriteValidationLog(
            $"Show returned: visible={window.IsVisible}, handle={new WindowInteropHelper(window).Handle}");

        if (performanceOptions is null && e.Args.Length > 0)
        {
            window.OpenPath(e.Args[0]);
        }

        Dispatcher.BeginInvoke(
            async () =>
            {
                WriteValidationLog(
                    $"Dispatcher ready: visible={window.IsVisible}, handle={new WindowInteropHelper(window).Handle}");

                if (performanceOptions is null)
                {
                    return;
                }

                var processStartToDispatcherReadyMs = (DateTime.Now - processStartedAt).TotalMilliseconds;
                var exitCode = await window.RunPerformanceValidationAsync(
                    performanceOptions,
                    processStartToLoadedMs ?? processStartToDispatcherReadyMs,
                    processStartToDispatcherReadyMs);
                Shutdown(exitCode);
            },
            DispatcherPriority.ApplicationIdle);
    }

    private static void WriteValidationLog(string message)
    {
        var path = Environment.GetEnvironmentVariable("BOK_WPF_VALIDATION_LOG");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
    }
}

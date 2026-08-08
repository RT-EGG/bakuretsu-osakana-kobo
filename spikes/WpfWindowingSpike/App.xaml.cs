using System.Windows;
using System.Windows.Threading;

namespace BakuretsuOsakanaKobo.Spikes.WpfWindowing;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var reportPath = TryGetOption(e.Args, "--validation-report");
        var window = new MainWindow(new MockPlaybackBackend());
        MainWindow = window;
        window.Show();

        if (reportPath is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            async () =>
            {
                var exitCode = await window.RunValidationAsync(reportPath);
                Shutdown(exitCode);
            },
            DispatcherPriority.ApplicationIdle);
    }

    private static string? TryGetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}

using System.Diagnostics;
using System.IO;
using System.Windows;

namespace BakuretsuOsakanaKobo.Spikes.SingleInstanceData;

public partial class App : Application
{
    private SingleInstanceCoordinator? _coordinator;
    private EventRecorder? _events;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = AppOptions.Parse(e.Args);
        _events = new EventRecorder(options.EventLogPath);

        if (options.DataValidationReportPath is not null)
        {
            var exitCode = await DataValidation.RunAsync(
                options.DataValidationReportPath,
                options.DataValidationBaseDirectory!);
            Shutdown(exitCode);
            return;
        }

        _coordinator = new SingleInstanceCoordinator(options.InstanceId);
        _coordinator.Diagnostic += Coordinator_OnDiagnostic;
        if (!_coordinator.IsPrimary)
        {
            _events.Record("secondary-send-start", new
            {
                processId = Environment.ProcessId,
                options.InstanceId,
                options.FileArguments,
            });
            var forwarded = await _coordinator.SendAsync(new LaunchRequest
            {
                SenderProcessId = Environment.ProcessId,
                FileArguments = options.FileArguments,
            });
            _events.Record("secondary-exit", new { forwarded, processId = Environment.ProcessId });
            Shutdown(forwarded ? 0 : 1);
            return;
        }

        _events.Record("primary-start", new
        {
            processId = Environment.ProcessId,
            options.InstanceId,
            options.FileArguments,
        });

        var window = new MainWindow(_events);
        MainWindow = window;
        _coordinator.RequestReceived += window.HandleLaunchRequestAsync;
        window.Show();
        await window.HandleLaunchRequestAsync(new LaunchRequest
        {
            SenderProcessId = Environment.ProcessId,
            FileArguments = options.FileArguments,
            IsInitialLaunch = true,
        });
        _coordinator.StartListening();

        if (options.ShutdownAfterMilliseconds is { } shutdownDelay)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(shutdownDelay);
                await Dispatcher.InvokeAsync(Shutdown);
            });
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_coordinator is not null && MainWindow is MainWindow window)
        {
            _coordinator.RequestReceived -= window.HandleLaunchRequestAsync;
        }

        if (_coordinator is not null)
        {
            _coordinator.Diagnostic -= Coordinator_OnDiagnostic;
        }
        _coordinator?.Dispose();
        _events?.Dispose();
        base.OnExit(e);
    }

    private void Coordinator_OnDiagnostic(object? sender, string message)
    {
        _events?.Record("coordinator", new { message, processId = Environment.ProcessId });
    }
}

internal sealed class AppOptions
{
    internal string InstanceId { get; private set; } = "product-default";
    internal string? EventLogPath { get; private set; }
    internal string? DataValidationReportPath { get; private set; }
    internal string? DataValidationBaseDirectory { get; private set; }
    internal int? ShutdownAfterMilliseconds { get; private set; }
    internal string[] FileArguments { get; private set; } = [];

    internal static AppOptions Parse(string[] args)
    {
        var result = new AppOptions();
        var files = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--instance-id" when index + 1 < args.Length:
                    result.InstanceId = args[++index];
                    break;
                case "--event-log" when index + 1 < args.Length:
                    result.EventLogPath = Path.GetFullPath(args[++index]);
                    break;
                case "--shutdown-after-ms" when index + 1 < args.Length &&
                    int.TryParse(args[++index], out var delay) && delay > 0:
                    result.ShutdownAfterMilliseconds = delay;
                    break;
                case "--data-validation-report" when index + 2 < args.Length:
                    result.DataValidationReportPath = Path.GetFullPath(args[++index]);
                    result.DataValidationBaseDirectory = Path.GetFullPath(args[++index]);
                    break;
                default:
                    files.Add(Path.GetFullPath(args[index]));
                    break;
            }
        }

        result.FileArguments = files.ToArray();
        return result;
    }
}

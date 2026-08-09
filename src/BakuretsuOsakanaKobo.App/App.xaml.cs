using System.Windows;
using System.Windows.Threading;
using BakuretsuOsakanaKobo.Infrastructure.Diagnostics;
using BakuretsuOsakanaKobo.Infrastructure.Errors;
using BakuretsuOsakanaKobo.Infrastructure.Persistence;
using BakuretsuOsakanaKobo.Playback;

namespace BakuretsuOsakanaKobo;

public partial class App : Application
{
    private IDiagnosticLog? _diagnosticLog;
    private ErrorReporter? _errorReporter;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var paths = PortableDataPaths.ForCurrentProcess();
        var logResult = FileDiagnosticLog.TryOpen(paths.LogsDirectory);
        _diagnosticLog = logResult.Log;

        var window = new MainWindow();
        MainWindow = window;
        _errorReporter = new ErrorReporter(_diagnosticLog, new MainWindowNotificationSink(window));
        IPlaybackBackend? playbackBackend = null;
        try
        {
            playbackBackend = new LibVlcPlaybackBackend(ReportPlaybackCallbackException);
        }
        catch (Exception exception)
        {
            _errorReporter.Report(
                new UserNotification(
                    UserNotificationSeverity.Error,
                    "動画再生機能を初期化できませんでした。",
                    "アプリを再起動し、改善しない場合は配置ファイルを確認してください。"),
                "playback-initialization-failed",
                exception.Message,
                exception);
        }

        window.ConfigureServices(paths, _errorReporter, playbackBackend);
        RegisterGlobalErrorHandlers();
        window.Show();

        _diagnosticLog.Write(new DiagnosticEvent(
            DiagnosticSeverity.Information,
            "application-started",
            "The application main window was shown."));

        if (logResult.Exception is not null)
        {
            _errorReporter.Report(
                new UserNotification(
                    UserNotificationSeverity.Warning,
                    "診断ログを保存できません。",
                    "アプリの配置先に書き込み権限があるか確認してください。"),
                "diagnostic-log-unavailable",
                logResult.Exception.Message,
                logResult.Exception,
                paths.LogsDirectory);
        }

        try
        {
            await InitializeSettingsAsync(paths, _errorReporter);
        }
        catch (Exception exception)
        {
            _errorReporter.Report(
                new UserNotification(
                    UserNotificationSeverity.Error,
                    "設定の初期化を完了できませんでした。",
                    "アプリは初期設定で続行します。"),
                "settings-initialization-failed",
                exception.Message,
                exception,
                paths.SettingsFilePath);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        UnregisterGlobalErrorHandlers();
        _diagnosticLog?.Write(new DiagnosticEvent(
            DiagnosticSeverity.Information,
            "application-exiting",
            "The application is exiting."));
        _diagnosticLog?.Dispose();
        _diagnosticLog = null;
        base.OnExit(e);
    }

    private static async Task InitializeSettingsAsync(
        PortableDataPaths paths,
        ErrorReporter errorReporter)
    {
        var settingsStore = new PortableJsonStore<AppSettings>(
            paths.SettingsFilePath,
            () => new AppSettings(),
            AppSettings.IsValid);
        var loadResult = await settingsStore.LoadOrDefaultAsync();

        if (loadResult.Warning is not null)
        {
            errorReporter.Report(
                new UserNotification(
                    UserNotificationSeverity.Warning,
                    "設定ファイルを読み込めなかったため、初期設定で起動しました。",
                    "必要な設定をもう一度指定してください。"),
                "settings-load-recovered",
                loadResult.Warning,
                targetPath: paths.SettingsFilePath);
        }

        if (!loadResult.UsedDefault)
        {
            return;
        }

        var saveResult = await settingsStore.SaveAsync(loadResult.Value);
        if (!saveResult.Success)
        {
            errorReporter.Report(
                new UserNotification(
                    UserNotificationSeverity.Warning,
                    "設定を保存できません。",
                    "再生は続行できます。アプリの配置先に書き込み権限があるか確認してください。"),
                "settings-save-failed",
                saveResult.ErrorMessage ?? "The settings save failed.",
                saveResult.Exception,
                paths.SettingsFilePath);
        }
    }

    private void RegisterGlobalErrorHandlers()
    {
        DispatcherUnhandledException += App_OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_OnUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_OnUnobservedTaskException;
    }

    private void UnregisterGlobalErrorHandlers()
    {
        DispatcherUnhandledException -= App_OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_OnUnobservedTaskException;
    }

    private void App_OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        _diagnosticLog?.Write(new DiagnosticEvent(
            DiagnosticSeverity.Critical,
            "dispatcher-unhandled-exception",
            e.Exception.Message,
            e.Exception));
        e.Handled = true;

        MessageBox.Show(
            MainWindow,
            "続行できない問題が発生したため、アプリを終了します。もう一度起動してください。",
            ApplicationInfo.DisplayName,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(1);
    }

    private void CurrentDomain_OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        _diagnosticLog?.Write(new DiagnosticEvent(
            DiagnosticSeverity.Critical,
            "application-domain-unhandled-exception",
            exception?.Message ?? "A non-Exception object reached the unhandled exception boundary.",
            exception));
    }

    private void TaskScheduler_OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        _diagnosticLog?.Write(new DiagnosticEvent(
            DiagnosticSeverity.Error,
            "unobserved-task-exception",
            e.Exception.Message,
            e.Exception));
        e.SetObserved();
    }

    private void ReportPlaybackCallbackException(Exception exception)
    {
        _diagnosticLog?.Write(new DiagnosticEvent(
            DiagnosticSeverity.Error,
            "playback-callback-failed",
            exception.Message,
            exception));
    }
}

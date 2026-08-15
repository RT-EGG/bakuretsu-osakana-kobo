using System.Threading.Channels;
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
    private SingleInstanceCoordinator? _singleInstanceCoordinator;
    private readonly Channel<LaunchRequest> _secondaryLaunchRequests =
        Channel.CreateBounded<LaunchRequest>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
    private readonly CancellationTokenSource _secondaryLaunchCancellation = new();
    private readonly TaskCompletionSource _initialLaunchHandled =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _secondaryLaunchTask;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceCoordinator = new SingleInstanceCoordinator();
        if (!_singleInstanceCoordinator.IsPrimary)
        {
            var forwarded = await _singleInstanceCoordinator.SendAsync(new LaunchRequest
            {
                SenderProcessId = Environment.ProcessId,
                FileArguments = e.Args,
            });
            Shutdown(forwarded ? 0 : 1);
            return;
        }

        _singleInstanceCoordinator.RequestReceived += HandleSecondaryLaunchRequestAsync;
        if (!_secondaryLaunchRequests.Writer.TryWrite(new LaunchRequest
            {
                SenderProcessId = Environment.ProcessId,
                FileArguments = e.Args,
                IsInitialLaunch = true,
            }))
        {
            throw new InvalidOperationException("The initial launch request could not be queued.");
        }
        _singleInstanceCoordinator.StartListening();

        var paths = PortableDataPaths.ForCurrentProcess();
        var logResult = FileDiagnosticLog.TryOpen(paths.LogsDirectory);
        _diagnosticLog = logResult.Log;

        var window = new MainWindow();
        MainWindow = window;
        _errorReporter = new ErrorReporter(_diagnosticLog, new MainWindowNotificationSink(window));
        var appSettings = await InitializeAppSettingsAsync(paths, _errorReporter);
        var videoProfiles = await InitializeVideoProfilesAsync(paths, _errorReporter);
        var recentFiles = await InitializeRecentFilesAsync(paths, _errorReporter);
        var playlist = await InitializePlaylistAsync(paths, _errorReporter);
        IPlaybackBackend? playbackBackend = null;
        IThumbnailGenerationService? thumbnailGenerationService = null;
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

        if (playbackBackend is not null)
        {
            try
            {
                thumbnailGenerationService = new ThumbnailGenerationService(ReportPlaybackCallbackException);
            }
            catch (Exception exception)
            {
                _errorReporter.ReportDiagnostic(
                    DiagnosticSeverity.Warning,
                    "thumbnail-generation-initialization-failed",
                    exception.Message,
                    exception);
            }
        }

        window.ConfigureServices(
            paths,
            _errorReporter,
            playbackBackend,
            videoProfiles,
            recentFiles,
            playlist,
            appSettings,
            thumbnailGenerationService);
        _singleInstanceCoordinator.Diagnostic += SingleInstanceCoordinator_OnDiagnostic;
        RegisterGlobalErrorHandlers();
        window.Show();

        _diagnosticLog.Write(new DiagnosticEvent(
            DiagnosticSeverity.Information,
            "application-started",
            "The application main window was shown."));

        _secondaryLaunchTask = ProcessSecondaryLaunchRequestsAsync(window, _secondaryLaunchCancellation.Token);
        _ = _secondaryLaunchTask.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        await _initialLaunchHandled.Task;

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

    }

    protected override void OnExit(ExitEventArgs e)
    {
        UnregisterGlobalErrorHandlers();
        _secondaryLaunchRequests.Writer.TryComplete();
        _secondaryLaunchCancellation.Cancel();
        if (_singleInstanceCoordinator is not null)
        {
            _singleInstanceCoordinator.RequestReceived -= HandleSecondaryLaunchRequestAsync;
            _singleInstanceCoordinator.Diagnostic -= SingleInstanceCoordinator_OnDiagnostic;
            _singleInstanceCoordinator.Dispose();
            _singleInstanceCoordinator = null;
        }
        _diagnosticLog?.Write(new DiagnosticEvent(
            DiagnosticSeverity.Information,
            "application-exiting",
            "The application is exiting."));
        _diagnosticLog?.Dispose();
        _diagnosticLog = null;
        _secondaryLaunchCancellation.Dispose();
        base.OnExit(e);
    }

    private Task HandleSecondaryLaunchRequestAsync(LaunchRequest request)
    {
        if (!_secondaryLaunchRequests.Writer.TryWrite(request))
        {
            throw new InvalidOperationException("The secondary launch request queue is unavailable or full.");
        }

        return Task.CompletedTask;
    }

    private async Task ProcessSecondaryLaunchRequestsAsync(
        MainWindow window,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _secondaryLaunchRequests.Reader.ReadAllAsync(cancellationToken))
            {
                await Dispatcher.InvokeAsync(
                    () => HandleLaunchRequestAsync(window, request),
                    DispatcherPriority.Normal,
                    cancellationToken).Task.Unwrap();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _initialLaunchHandled.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            _initialLaunchHandled.TrySetException(exception);
            throw;
        }
    }

    private async Task HandleLaunchRequestAsync(MainWindow window, LaunchRequest request)
    {
        await window.HandleLaunchRequestAsync(request);
        var action = request.FileArguments.Length switch
        {
            0 => "activate-only",
            1 => "open-one",
            _ => "ignore-multiple",
        };
        _diagnosticLog?.Write(new DiagnosticEvent(
            DiagnosticSeverity.Information,
            "launch-request-handled",
            $"Launch request action: {action}; initial: {request.IsInitialLaunch}; sender PID: {request.SenderProcessId}.",
            TargetPath: request.FileArguments.Length == 1 ? request.FileArguments[0] : null));
        if (request.IsInitialLaunch)
        {
            _initialLaunchHandled.TrySetResult();
        }
    }

    private void SingleInstanceCoordinator_OnDiagnostic(
        object? sender,
        SingleInstanceDiagnosticEventArgs eventArgs)
    {
        _diagnosticLog?.Write(new DiagnosticEvent(
            eventArgs.Exception is null ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
            $"single-instance-{eventArgs.EventCode}",
            eventArgs.Message ?? eventArgs.EventCode,
            eventArgs.Exception));
    }

    private static async Task<AppSettingsRepository?> InitializeAppSettingsAsync(
        PortableDataPaths paths,
        ErrorReporter errorReporter)
    {
        var repository = new AppSettingsRepository(paths.SettingsFilePath);
        try
        {
            var loadResult = await repository.LoadAsync();
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

            if (loadResult.UsedDefault)
            {
                var saveResult = await repository.SaveAsync();
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

            return repository;
        }
        catch (Exception exception)
        {
            repository.Dispose();
            errorReporter.Report(
                new UserNotification(
                    UserNotificationSeverity.Warning,
                    "設定の初期化を完了できませんでした。",
                    "アプリは初期設定で続行します。"),
                "settings-initialization-failed",
                exception.Message,
                exception,
                paths.SettingsFilePath);
            return null;
        }
    }

    private static async Task<VideoProfileRepository?> InitializeVideoProfilesAsync(
        PortableDataPaths paths,
        ErrorReporter errorReporter)
    {
        var repository = new VideoProfileRepository(paths.VideoProfilesFilePath);
        try
        {
            var loadResult = await repository.LoadAsync();
            if (loadResult.Warning is not null)
            {
                errorReporter.Report(
                    new UserNotification(
                        UserNotificationSeverity.Warning,
                        "動画ごとの設定を読み込めなかったため、初期値で続行します。",
                        "必要な動画の音量、ミュート、再生開始位置をもう一度指定してください。"),
                    "video-profiles-load-recovered",
                    loadResult.Warning,
                    targetPath: paths.VideoProfilesFilePath);
            }

            return repository;
        }
        catch (Exception exception)
        {
            repository.Dispose();
            errorReporter.Report(
                new UserNotification(
                    UserNotificationSeverity.Warning,
                    "動画ごとの設定を初期化できませんでした。",
                    "設定は保存されませんが、動画の再生は続行できます。"),
                "video-profiles-initialization-failed",
                exception.Message,
                exception,
                paths.VideoProfilesFilePath);
            return null;
        }
    }

    private static async Task<RecentFileRepository?> InitializeRecentFilesAsync(
        PortableDataPaths paths,
        ErrorReporter errorReporter)
    {
        var repository = new RecentFileRepository(paths.RecentFilesFilePath);
        try
        {
            var loadResult = await repository.LoadAsync();
            if (loadResult.Warning is not null)
            {
                errorReporter.Report(
                    new UserNotification(
                        UserNotificationSeverity.Warning,
                        "最近開いたファイルの履歴を読み込めなかったため、空の履歴で続行します。",
                        "動画を開くと新しい履歴を保存します。"),
                    "recent-files-load-recovered",
                    loadResult.Warning,
                    targetPath: paths.RecentFilesFilePath);
            }

            return repository;
        }
        catch (Exception exception)
        {
            repository.Dispose();
            errorReporter.Report(
                new UserNotification(
                    UserNotificationSeverity.Warning,
                    "最近開いたファイルの履歴を初期化できませんでした。",
                    "履歴は保存されませんが、動画の再生は続行できます。"),
                "recent-files-initialization-failed",
                exception.Message,
                exception,
                paths.RecentFilesFilePath);
            return null;
        }
    }

    private static async Task<PlaylistRepository?> InitializePlaylistAsync(
        PortableDataPaths paths,
        ErrorReporter errorReporter)
    {
        var repository = new PlaylistRepository(paths.PlaylistFilePath);
        try
        {
            var loadResult = await repository.LoadAsync();
            if (loadResult.Warning is not null)
            {
                errorReporter.Report(
                    new UserNotification(
                        UserNotificationSeverity.Warning,
                        "プレイリストを読み込めなかったため、空の状態で続行します。",
                        "必要な動画をもう一度追加してください。"),
                    "playlist-load-recovered",
                    loadResult.Warning,
                    targetPath: paths.PlaylistFilePath);
            }

            return repository;
        }
        catch (Exception exception)
        {
            repository.Dispose();
            errorReporter.Report(
                new UserNotification(
                    UserNotificationSeverity.Warning,
                    "プレイリストを初期化できませんでした。",
                    "プレイリストは保存されませんが、動画の再生は続行できます。"),
                "playlist-initialization-failed",
                exception.Message,
                exception,
                paths.PlaylistFilePath);
            return null;
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

using System.IO;
using System.Net.Http;
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
    private readonly CancellationTokenSource _startupInitializationCancellation = new();
    private readonly TaskCompletionSource _initialLaunchHandled =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _secondaryLaunchTask;
    private HttpClient? _updateHttpClient;
    private HttpClient? _updateDownloadHttpClient;
    private StartupPerformanceTrace? _startupPerformanceTrace;

    protected override async void OnStartup(StartupEventArgs e)
    {
        _startupPerformanceTrace = StartupPerformanceTrace.TryCreate(e.Args);
        _startupPerformanceTrace?.Record("onStartupEntered");
        base.OnStartup(e);

        var updateStartupArguments = UpdateStartupResultReader.ExtractArguments(e.Args);

        _singleInstanceCoordinator = new SingleInstanceCoordinator();
        if (!_singleInstanceCoordinator.IsPrimary)
        {
            var forwarded = await _singleInstanceCoordinator.SendAsync(new LaunchRequest
            {
                SenderProcessId = Environment.ProcessId,
                FileArguments = updateStartupArguments.LaunchArguments,
            });
            Shutdown(forwarded ? 0 : 1);
            return;
        }
        _startupPerformanceTrace?.Record("primaryInstanceReady");

        _singleInstanceCoordinator.RequestReceived += HandleSecondaryLaunchRequestAsync;
        if (!_secondaryLaunchRequests.Writer.TryWrite(new LaunchRequest
            {
            SenderProcessId = Environment.ProcessId,
            FileArguments = updateStartupArguments.LaunchArguments,
                IsInitialLaunch = true,
            }))
        {
            throw new InvalidOperationException("The initial launch request could not be queued.");
        }
        _singleInstanceCoordinator.StartListening();

        var paths = PortableDataPaths.ForCurrentProcess();
        var logResult = FileDiagnosticLog.TryOpen(paths.LogsDirectory);
        _diagnosticLog = logResult.Log;
        _startupPerformanceTrace?.Record("portablePathsAndLogReady");

        _startupPerformanceTrace?.Record("windowConstructionStarted");
        var window = new MainWindow();
        MainWindow = window;
        _errorReporter = new ErrorReporter(_diagnosticLog, new MainWindowNotificationSink(window));
        _startupPerformanceTrace?.Record("windowConstructionCompleted");
        window.ConfigureStartupShell(paths, _errorReporter);
        _singleInstanceCoordinator.Diagnostic += SingleInstanceCoordinator_OnDiagnostic;
        RegisterGlobalErrorHandlers();
        var shellInteractive = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? contentRenderedHandler = null;
        contentRenderedHandler = (_, _) =>
        {
            window.ContentRendered -= contentRenderedHandler;
            _startupPerformanceTrace?.Record("contentRendered");
            window.Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(() =>
                {
                    _startupPerformanceTrace?.Record("dispatcherIdle");
                    _startupPerformanceTrace?.Record("shellInteractive");
                    shellInteractive.TrySetResult();
                }));
        };
        window.ContentRendered += contentRenderedHandler;

        _startupPerformanceTrace?.Record("showStarted");
        window.Show();
        _startupPerformanceTrace?.Record("showReturned");

        _diagnosticLog.Write(new DiagnosticEvent(
            DiagnosticSeverity.Information,
            "application-shell-shown",
            "The application shell was shown before deferred service initialization."));

        var startupInitializationTask = CompleteStartupInitializationAsync(
            window,
            paths,
            updateStartupArguments.ResultPath,
            logResult.Exception,
            shellInteractive.Task,
            _startupInitializationCancellation.Token);
        window.TrackStartupInitialization(
            startupInitializationTask,
            _startupInitializationCancellation.Cancel);
        await startupInitializationTask;
    }

    private async Task CompleteStartupInitializationAsync(
        MainWindow window,
        PortableDataPaths paths,
        string? updateResultPath,
        Exception? diagnosticLogException,
        Task shellInteractiveTask,
        CancellationToken cancellationToken)
    {
        AppSettingsRepository? appSettings = null;
        VideoProfileRepository? videoProfiles = null;
        RecentFileRepository? recentFiles = null;
        PlaylistRepository? playlist = null;
        IPlaybackBackend? playbackBackend = null;
        IThumbnailGenerationService? thumbnailGenerationService = null;
        Task<AppSettingsRepository?>? appSettingsTask = null;
        Task<VideoProfileRepository?>? videoProfilesTask = null;
        Task<RecentFileRepository?>? recentFilesTask = null;
        Task<PlaylistRepository?>? playlistTask = null;
        Task<IPlaybackBackend?>? playbackTask = null;
        var ownershipTransferred = false;

        try
        {
            await shellInteractiveTask.WaitAsync(cancellationToken);
            _startupPerformanceTrace?.Record("updateResultReadStarted");
            var updateResultTask = TraceCompletionAsync(
                UpdateStartupResultReader.ReadAsync(
                    updateResultPath,
                    UpdateApplicationCoordinator.GetDefaultWorkingRoot()),
                "updateResultReadCompleted");
            _startupPerformanceTrace?.Record("appSettingsInitializationStarted");
            appSettingsTask = TraceCompletionAsync(
                InitializeAppSettingsAsync(paths, _errorReporter!),
                "appSettingsInitializationCompleted");
            _startupPerformanceTrace?.Record("videoProfilesInitializationStarted");
            videoProfilesTask = TraceCompletionAsync(
                InitializeVideoProfilesAsync(paths, _errorReporter!),
                "videoProfilesInitializationCompleted");
            _startupPerformanceTrace?.Record("recentFilesInitializationStarted");
            recentFilesTask = TraceCompletionAsync(
                InitializeRecentFilesAsync(paths, _errorReporter!),
                "recentFilesInitializationCompleted");
            _startupPerformanceTrace?.Record("playlistInitializationStarted");
            playlistTask = TraceCompletionAsync(
                InitializePlaylistAsync(paths, _errorReporter!),
                "playlistInitializationCompleted");
            _startupPerformanceTrace?.Record("playbackInitializationStarted");
            playbackTask = Task.Run<IPlaybackBackend?>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var backend = new LibVlcPlaybackBackend(ReportPlaybackCallbackException);
                if (!cancellationToken.IsCancellationRequested)
                {
                    return backend;
                }

                backend.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }, CancellationToken.None);
            playbackTask = TraceCompletionAsync(playbackTask, "playbackInitializationCompleted");

            var updateStartupResult = await updateResultTask;
            appSettings = await appSettingsTask;
            videoProfiles = await videoProfilesTask;
            recentFiles = await recentFilesTask;
            playlist = await playlistTask;
            try
            {
                playbackBackend = await playbackTask;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _errorReporter!.Report(
                    new UserNotification(
                        UserNotificationSeverity.Error,
                        "動画再生機能を初期化できませんでした。",
                        "アプリを再起動し、改善しない場合は配置ファイルを確認してください。"),
                    "playback-initialization-failed",
                    exception.Message,
                    exception);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (playbackBackend is not null)
            {
                _startupPerformanceTrace?.Record("thumbnailInitializationStarted");
                try
                {
                    thumbnailGenerationService = new ThumbnailGenerationService(ReportPlaybackCallbackException);
                }
                catch (Exception exception)
                {
                    _errorReporter!.ReportDiagnostic(
                        DiagnosticSeverity.Warning,
                        "thumbnail-generation-initialization-failed",
                        exception.Message,
                        exception);
                }
                finally
                {
                    _startupPerformanceTrace?.Record("thumbnailInitializationCompleted");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            _startupPerformanceTrace?.Record("serviceConfigurationStarted");
            ownershipTransferred = true;
            window.ConfigureServices(
                paths,
                _errorReporter!,
                playbackBackend,
                videoProfiles,
                recentFiles,
                playlist,
                appSettings,
                thumbnailGenerationService,
                CreateUpdateCheckService(_errorReporter!, out var currentVersion),
                currentVersion,
                CreateApplicationUpdateCoordinator(paths));
            _startupPerformanceTrace?.Record("serviceConfigurationCompleted");
            window.PresentApplicationUpdateResult(updateStartupResult);

            _diagnosticLog?.Write(new DiagnosticEvent(
                DiagnosticSeverity.Information,
                "application-started",
                "Deferred application service initialization completed."));

            _secondaryLaunchTask = ProcessSecondaryLaunchRequestsAsync(window, _secondaryLaunchCancellation.Token);
            _ = _secondaryLaunchTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            await _initialLaunchHandled.Task;
            _startupPerformanceTrace?.Record("initialLaunchHandled");
            window.StartAutomaticUpdateCheck();
            _startupPerformanceTrace?.Record("automaticUpdateCheckStarted");

            if (diagnosticLogException is not null)
            {
                _errorReporter!.Report(
                    new UserNotification(
                        UserNotificationSeverity.Warning,
                        "診断ログを保存できません。",
                        "アプリの配置先に書き込み権限があるか確認してください。"),
                    "diagnostic-log-unavailable",
                    diagnosticLogException.Message,
                    diagnosticLogException,
                    paths.LogsDirectory);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _diagnosticLog?.Write(new DiagnosticEvent(
                DiagnosticSeverity.Information,
                "startup-initialization-canceled",
                "Deferred startup initialization was canceled during application shutdown."));
        }
        catch (Exception exception)
        {
            _errorReporter?.Report(
                new UserNotification(
                    UserNotificationSeverity.Error,
                    "アプリの初期化を完了できませんでした。",
                    "アプリを終了して、もう一度起動してください。"),
                "startup-initialization-failed",
                exception.Message,
                exception);
        }
        finally
        {
            if (!ownershipTransferred)
            {
                appSettings ??= await GetResourceAfterPartialInitializationAsync(appSettingsTask);
                videoProfiles ??= await GetResourceAfterPartialInitializationAsync(videoProfilesTask);
                recentFiles ??= await GetResourceAfterPartialInitializationAsync(recentFilesTask);
                playlist ??= await GetResourceAfterPartialInitializationAsync(playlistTask);
                playbackBackend ??= await GetResourceAfterPartialInitializationAsync(playbackTask);
                if (thumbnailGenerationService is not null)
                {
                    await thumbnailGenerationService.DisposeAsync();
                }

                playbackBackend?.Dispose();
                videoProfiles?.Dispose();
                recentFiles?.Dispose();
                playlist?.Dispose();
                appSettings?.Dispose();
            }
        }
    }

    private static async Task<T?> GetResourceAfterPartialInitializationAsync<T>(Task<T?>? task)
        where T : class
    {
        if (task is null)
        {
            return null;
        }

        try
        {
            return await task;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<T> TraceCompletionAsync<T>(Task<T> task, string completionMilestone)
    {
        try
        {
            return await task;
        }
        finally
        {
            _startupPerformanceTrace?.Record(completionMilestone);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _startupInitializationCancellation.Cancel();
        _startupPerformanceTrace?.WriteIncompleteOnExit();
        _startupPerformanceTrace = null;
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
        _updateHttpClient?.Dispose();
        _updateHttpClient = null;
        _updateDownloadHttpClient?.Dispose();
        _updateDownloadHttpClient = null;
        _secondaryLaunchCancellation.Dispose();
        _startupInitializationCancellation.Dispose();
        base.OnExit(e);
    }

    private IUpdateCheckService? CreateUpdateCheckService(
        ErrorReporter errorReporter,
        out SemanticVersion? currentVersion)
    {
        if (!ApplicationInfo.TryGetCurrentSemanticVersion(out currentVersion) || currentVersion is null)
        {
            errorReporter.ReportDiagnostic(
                DiagnosticSeverity.Warning,
                "application-version-invalid",
                "The assembly informational version was not a valid semantic version; update checks are disabled.");
            return null;
        }

        _updateHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        return new GitHubReleaseClient(_updateHttpClient);
    }

    private UpdateApplicationCoordinator CreateApplicationUpdateCoordinator(PortableDataPaths paths)
    {
        _updateDownloadHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        return new UpdateApplicationCoordinator(
            new UpdatePackageDownloader(_updateDownloadHttpClient),
            Path.Combine(paths.ExecutableDirectory, "updater"),
            UpdateApplicationCoordinator.GetDefaultWorkingRoot());
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
        if (request.IsInitialLaunch)
        {
            _startupPerformanceTrace?.Record("initialLaunchHandlingStarted");
        }

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

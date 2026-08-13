using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BakuretsuOsakanaKobo.Infrastructure.Errors;
using BakuretsuOsakanaKobo.Infrastructure.Persistence;
using BakuretsuOsakanaKobo.Playback;
using Microsoft.Win32;

namespace BakuretsuOsakanaKobo;

public partial class MainWindow : Window
{
    private static readonly TimeSpan PlaybackTimelineRefreshInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan TemporaryPlaybackRateReleasePollInterval = TimeSpan.FromMilliseconds(25);
    private const int WindowMessageActivateApplication = 0x001C;
    internal static readonly TimeSpan VideoProfileSaveDelay = TimeSpan.FromSeconds(3);

    internal bool IsTemporaryPlaybackRatePending => _temporaryPlaybackRateGesture.IsPending;

    internal bool IsTemporaryPlaybackRateActive => _temporaryPlaybackRateGesture.IsActive;

    internal bool IsFullscreen => _isFullscreen;

    private readonly DispatcherTimer _playbackTimelineTimer;
    private readonly DispatcherTimer _videoProfileSaveTimer;
    private readonly DispatcherTimer _temporaryPlaybackRateTimer;
    private readonly DispatcherTimer _temporaryPlaybackRateReleaseTimer;
    private readonly DispatcherTimer _fullscreenControlsTimer;
    private readonly TemporaryPlaybackRateGesture _temporaryPlaybackRateGesture = new();
    private readonly FullscreenControlsState _fullscreenControlsState = new();
    private PortableDataPaths? _paths;
    private ErrorReporter? _errorReporter;
    private IPlaybackBackend? _playbackBackend;
    private VideoProfileRepository? _videoProfiles;
    private RecentFileRepository? _recentFiles;
    private PlaylistRepository? _playlist;
    private PlaylistWindow? _playlistWindow;
    private CancellationTokenSource? _openCancellation;
    private Task? _openTask;
    private bool _isOpeningVideo;
    private bool _isUpdatingSeekSlider;
    private bool _isUpdatingVolumeSlider;
    private bool _isSeekDragging;
    private bool _hasPlaybackError;
    private long _videoProfileRevision;
    private long _savedVideoProfileRevision;
    private Task<JsonSaveResult>? _videoProfileSaveTask;
    private Task? _recentFileMutationTask;
    private Task? _playlistMutationTask;
    private DateTime _seekPresentationHoldUntilUtc;
    private bool _closeRequested;
    private bool _allowClose;
    private bool _disposed;
    private bool _isFullscreen;
    private WindowState _windowStateBeforeFullscreen;
    private WindowStyle _windowStyleBeforeFullscreen;
    private ResizeMode _resizeModeBeforeFullscreen;
    private HwndSource? _windowSource;

    public MainWindow()
    {
        InitializeComponent();
        _playbackTimelineTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = PlaybackTimelineRefreshInterval,
        };
        _playbackTimelineTimer.Tick += PlaybackTimelineTimer_OnTick;
        _videoProfileSaveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = VideoProfileSaveDelay,
        };
        _videoProfileSaveTimer.Tick += VideoProfileSaveTimer_OnTick;
        _temporaryPlaybackRateTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = TemporaryPlaybackRateGesture.HoldDuration,
        };
        _temporaryPlaybackRateTimer.Tick += TemporaryPlaybackRateTimer_OnTick;
        _temporaryPlaybackRateReleaseTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = TemporaryPlaybackRateReleasePollInterval,
        };
        _temporaryPlaybackRateReleaseTimer.Tick += TemporaryPlaybackRateReleaseTimer_OnTick;
        _fullscreenControlsTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = FullscreenControlsState.AutoHideDelay,
        };
        _fullscreenControlsTimer.Tick += FullscreenControlsTimer_OnTick;
        SeekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(SeekSlider_OnDragStarted));
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(SeekSlider_OnDragCompleted));
    }

    internal void ConfigureServices(
        PortableDataPaths paths,
        ErrorReporter errorReporter,
        IPlaybackBackend? playbackBackend,
        VideoProfileRepository? videoProfiles = null,
        RecentFileRepository? recentFiles = null,
        PlaylistRepository? playlist = null)
    {
        _paths = paths;
        _errorReporter = errorReporter;
        _playbackBackend = playbackBackend;
        _videoProfiles = videoProfiles;
        _recentFiles = recentFiles;
        _playlist = playlist;
        OpenVideoMenuItem.IsEnabled = playbackBackend is not null;
        RebuildRecentFilesMenu();

        if (playbackBackend is LibVlcPlaybackBackend libVlcBackend)
        {
            VideoView.MediaPlayer = libVlcBackend.MediaPlayer;
        }

        if (playbackBackend is not null)
        {
            playbackBackend.ErrorOccurred += PlaybackBackend_OnErrorOccurred;
            playbackBackend.StateChanged += PlaybackBackend_OnStateChanged;
            _playbackTimelineTimer.Start();
        }

        UpdatePlaybackButton();
        UpdatePlaybackTimeline();
        UpdateVolumeControls();
        UpdatePlaybackRateControls();
    }

    internal void ShowNotification(UserNotification notification)
    {
        (NotificationBorder.Background, NotificationBorder.BorderBrush) = notification.Severity switch
        {
            UserNotificationSeverity.Information =>
                (new SolidColorBrush(Color.FromRgb(0x18, 0x2A, 0x38)), new SolidColorBrush(Color.FromRgb(0x41, 0x76, 0x9B))),
            UserNotificationSeverity.Warning =>
                (new SolidColorBrush(Color.FromRgb(0x2B, 0x21, 0x15)), new SolidColorBrush(Color.FromRgb(0x9A, 0x6A, 0x2D))),
            _ =>
                (new SolidColorBrush(Color.FromRgb(0x2B, 0x20, 0x26)), new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x79))),
        };
        NotificationMessageText.Text = notification.Message;
        NotificationActionText.Text = notification.SuggestedAction;
        NotificationBorder.Visibility = Visibility.Visible;
    }

    protected override void OnSourceInitialized(EventArgs eventArgs)
    {
        base.OnSourceInitialized(eventArgs);
        _windowSource = (HwndSource?)PresentationSource.FromVisual(this);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private void OpenDataFolderMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_paths is null || _errorReporter is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_paths.DataDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _paths.DataDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _errorReporter.Report(
                new UserNotification(
                    UserNotificationSeverity.Warning,
                    "データフォルダーを開けませんでした。",
                    "アプリの配置先とアクセス権限を確認してください。"),
                "open-data-folder-failed",
                exception.Message,
                exception,
                _paths.DataDirectory);
        }
    }

    private async void OpenVideoMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_playbackBackend is null || _openTask is not null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "動画を開く",
            Filter = "対応する動画 (*.mp4;*.wmv)|*.mp4;*.wmv|MP4動画 (*.mp4)|*.mp4|WMV動画 (*.wmv)|*.wmv",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await OpenVideoFromUserRequestAsync(dialog.FileName);
    }

    private async void RecentFilesMenuItem_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        if (eventArgs.OriginalSource is not MenuItem menuItem || _closeRequested)
        {
            return;
        }

        switch (menuItem.Tag)
        {
            case OpenRecentFileAction openAction:
                eventArgs.Handled = true;
                await OpenVideoFromUserRequestAsync(openAction.Path);
                if (!_closeRequested)
                {
                    RebuildRecentFilesMenu();
                }
                break;
            case RemoveRecentFileAction removeAction:
                eventArgs.Handled = true;
                await RunRecentFileMutationAsync(
                    repository => repository.RemoveAsync(removeAction.Path));
                break;
            case RemoveAllMissingRecentFilesAction:
                eventArgs.Handled = true;
                await RunRecentFileMutationAsync(repository => repository.RemoveMissingAsync());
                break;
            case ClearRecentFilesAction:
                eventArgs.Handled = true;
                await RunRecentFileMutationAsync(repository => repository.ClearAsync());
                break;
        }
    }

    private void PlaylistMenuItem_OnCheckedChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (PlaylistMenuItem.IsChecked)
        {
            ShowPlaylistWindow();
            return;
        }

        _playlistWindow?.Close();
    }

    private void ShowPlaylistWindow()
    {
        if (_playlistWindow is not null)
        {
            _playlistWindow.Activate();
            return;
        }

        var snapshot = _playlist?.GetSnapshot() ?? new PlaylistSnapshot([], false);
        var playlistWindow = new PlaylistWindow(
            snapshot.Entries,
            snapshot.Loop,
            canPersist: _playlist is not null && _playlistMutationTask is null,
            currentMediaPath: _playbackBackend?.CurrentPath)
        {
            Owner = this,
        };
        playlistWindow.LoopChanged += PlaylistWindow_OnLoopChanged;
        playlistWindow.EntriesAddRequested += PlaylistWindow_OnEntriesAddRequested;
        playlistWindow.EntriesRemoveRequested += PlaylistWindow_OnEntriesRemoveRequested;
        playlistWindow.EntryMoveRequested += PlaylistWindow_OnEntryMoveRequested;
        playlistWindow.Closed += PlaylistWindow_OnClosed;
        _playlistWindow = playlistWindow;
        playlistWindow.Show();
    }

    private async void PlaylistWindow_OnLoopChanged(
        object? sender,
        PlaylistLoopChangedEventArgs eventArgs)
    {
        var playlist = _playlist;
        var playlistWindow = sender as PlaylistWindow;
        if (playlist is null || _closeRequested)
        {
            if (playlistWindow is not null)
            {
                playlistWindow.CompletePersistence(playlist?.GetSnapshot().Entries ?? []);
            }
            return;
        }

        if (_playlistMutationTask is not null)
        {
            return;
        }

        Task<JsonSaveResult> mutationTask;
        try
        {
            mutationTask = playlist.SetLoopAsync(eventArgs.Loop);
        }
        catch (Exception exception)
        {
            ReportPlaylistSaveFailure(exception, playlist.FilePath);
            playlistWindow?.CompletePersistence(
                playlist.GetSnapshot().Entries,
                enablePersistence: true);
            return;
        }

        _playlistMutationTask = mutationTask;
        try
        {
            var result = await mutationTask;
            if (!result.Success)
            {
                ReportPlaylistSaveFailure(
                    result.Exception ?? new IOException(result.ErrorMessage),
                    playlist.FilePath);
            }
        }
        catch (Exception exception)
        {
            ReportPlaylistSaveFailure(exception, playlist.FilePath);
        }
        finally
        {
            if (ReferenceEquals(_playlistMutationTask, mutationTask))
            {
                _playlistMutationTask = null;
            }

            if (!_closeRequested)
            {
                var entries = playlist.GetSnapshot().Entries;
                playlistWindow?.CompletePersistence(entries, enablePersistence: true);
                if (_playlistWindow is { } activeWindow &&
                    !ReferenceEquals(activeWindow, playlistWindow))
                {
                    activeWindow.CompletePersistence(entries, enablePersistence: true);
                }
            }
        }
    }

    private async void PlaylistWindow_OnEntriesAddRequested(
        object? sender,
        PlaylistEntriesAddRequestedEventArgs eventArgs)
    {
        var playlist = _playlist;
        var playlistWindow = sender as PlaylistWindow;
        if (playlist is null || _closeRequested)
        {
            playlistWindow?.CompletePersistence(playlist?.GetSnapshot().Entries ?? []);
            return;
        }

        if (_playlistMutationTask is not null)
        {
            return;
        }

        Task<JsonSaveResult> mutationTask;
        try
        {
            mutationTask = playlist.AddEntriesAsync(eventArgs.Paths);
        }
        catch (Exception exception)
        {
            ReportPlaylistSaveFailure(exception, playlist.FilePath);
            playlistWindow?.CompletePersistence(
                playlist.GetSnapshot().Entries,
                enablePersistence: true);
            return;
        }

        _playlistMutationTask = mutationTask;
        try
        {
            var result = await mutationTask;
            if (!result.Success)
            {
                ReportPlaylistSaveFailure(
                    result.Exception ?? new IOException(result.ErrorMessage),
                    playlist.FilePath);
            }
        }
        catch (Exception exception)
        {
            ReportPlaylistSaveFailure(exception, playlist.FilePath);
        }
        finally
        {
            if (ReferenceEquals(_playlistMutationTask, mutationTask))
            {
                _playlistMutationTask = null;
            }

            if (!_closeRequested)
            {
                var entries = playlist.GetSnapshot().Entries;
                playlistWindow?.CompletePersistence(
                    entries,
                    eventArgs.Paths.Count,
                    eventArgs.RejectedCount,
                    enablePersistence: true);
                if (_playlistWindow is { } activeWindow &&
                    !ReferenceEquals(activeWindow, playlistWindow))
                {
                    activeWindow.CompletePersistence(entries, enablePersistence: true);
                }
            }
        }
    }

    private async void PlaylistWindow_OnEntriesRemoveRequested(
        object? sender,
        PlaylistEntriesRemoveRequestedEventArgs eventArgs)
    {
        await PersistPlaylistMutationAsync(
            sender as PlaylistWindow,
            playlist => playlist.RemoveAtIndicesAsync(eventArgs.Indices),
            $"{eventArgs.Indices.Count}件をプレイリストから削除しました。元の動画ファイルは削除していません。");
    }

    private async void PlaylistWindow_OnEntryMoveRequested(
        object? sender,
        PlaylistEntryMoveRequestedEventArgs eventArgs)
    {
        await PersistPlaylistMutationAsync(
            sender as PlaylistWindow,
            playlist => playlist.MoveToInsertionIndexAsync(
                eventArgs.SourceIndex,
                eventArgs.InsertionIndex),
            "再生順を変更しました。");
    }

    private async Task PersistPlaylistMutationAsync(
        PlaylistWindow? playlistWindow,
        Func<PlaylistRepository, Task<JsonSaveResult>> mutation,
        string notification)
    {
        var playlist = _playlist;
        if (playlist is null || _closeRequested)
        {
            playlistWindow?.CompletePersistence(playlist?.GetSnapshot().Entries ?? []);
            return;
        }

        if (_playlistMutationTask is not null)
        {
            return;
        }

        Task<JsonSaveResult> mutationTask;
        try
        {
            mutationTask = mutation(playlist);
        }
        catch (Exception exception)
        {
            ReportPlaylistSaveFailure(exception, playlist.FilePath);
            playlistWindow?.CompletePersistence(
                playlist.GetSnapshot().Entries,
                enablePersistence: true);
            return;
        }

        _playlistMutationTask = mutationTask;
        var saved = false;
        try
        {
            var result = await mutationTask;
            saved = result.Success;
            if (!result.Success)
            {
                ReportPlaylistSaveFailure(
                    result.Exception ?? new IOException(result.ErrorMessage),
                    playlist.FilePath);
            }
        }
        catch (Exception exception)
        {
            ReportPlaylistSaveFailure(exception, playlist.FilePath);
        }
        finally
        {
            if (ReferenceEquals(_playlistMutationTask, mutationTask))
            {
                _playlistMutationTask = null;
            }

            if (!_closeRequested)
            {
                var entries = playlist.GetSnapshot().Entries;
                playlistWindow?.CompletePersistence(
                    entries,
                    enablePersistence: true,
                    notification: saved ? notification : null);
                if (_playlistWindow is { } activeWindow &&
                    !ReferenceEquals(activeWindow, playlistWindow))
                {
                    activeWindow.CompletePersistence(entries, enablePersistence: true);
                }
            }
        }
    }

    private void PlaylistWindow_OnClosed(object? sender, EventArgs eventArgs)
    {
        if (sender is PlaylistWindow playlistWindow)
        {
            playlistWindow.LoopChanged -= PlaylistWindow_OnLoopChanged;
            playlistWindow.EntriesAddRequested -= PlaylistWindow_OnEntriesAddRequested;
            playlistWindow.EntriesRemoveRequested -= PlaylistWindow_OnEntriesRemoveRequested;
            playlistWindow.EntryMoveRequested -= PlaylistWindow_OnEntryMoveRequested;
            playlistWindow.Closed -= PlaylistWindow_OnClosed;
        }

        _playlistWindow = null;
        PlaylistMenuItem.IsChecked = false;
    }

    private async Task OpenVideoFromUserRequestAsync(string path)
    {
        if (_playbackBackend is null || _openTask is not null || _closeRequested)
        {
            return;
        }

        _isOpeningVideo = true;
        var openTask = OpenVideoAsync(path);
        _openTask = openTask;
        try
        {
            UpdatePlaybackButton();
            UpdatePlaybackTimeline();
            UpdateVolumeControls();
            UpdatePlaybackRateControls();
            await openTask;
        }
        finally
        {
            if (ReferenceEquals(_openTask, openTask))
            {
                _openTask = null;
            }

            _isOpeningVideo = false;
            if (!_closeRequested)
            {
                UpdatePlaybackButton();
                UpdatePlaybackTimeline();
                UpdateVolumeControls();
                UpdatePlaybackRateControls();
            }
        }
    }

    internal async Task OpenVideoAsync(string path)
    {
        if (_playbackBackend is null || _errorReporter is null)
        {
            return;
        }

        EndTemporaryPlaybackRateGesture();
        OpenVideoMenuItem.IsEnabled = false;
        PlayPauseButton.IsEnabled = false;
        UpdatePlaybackTimeline();
        var hadCurrentVideo = _playbackBackend.CurrentPath is not null;
        if (!hadCurrentVideo)
        {
            EmptyStateTitleText.Text = "動画を確認しています";
            EmptyStateDescriptionText.Text = "対応形式と再生準備を確認しています…";
        }

        using var openCancellation = new CancellationTokenSource();
        _openCancellation = openCancellation;

        try
        {
            await FlushVideoProfilesAsync();
            var initialState = GetInitialPlaybackState(path);
            if (await _playbackBackend.OpenAndPlayAsync(
                    path,
                    initialState,
                    openCancellation.Token))
            {
                _hasPlaybackError = false;
                EmptyStatePanel.Visibility = Visibility.Collapsed;
                Title = $"{Path.GetFileName(path)} - {ApplicationInfo.DisplayName}";
                NotificationBorder.Visibility = Visibility.Collapsed;
                _playlistWindow?.UpdateCurrentMedia(path);
                await RecordRecentFileAsync(path);
            }
            else if (!hadCurrentVideo)
            {
                ShowEmptyState();
            }
        }
        catch (OperationCanceledException) when (openCancellation.IsCancellationRequested)
        {
            if (!hadCurrentVideo)
            {
                ShowEmptyState();
            }
        }
        catch (Exception exception)
        {
            _errorReporter.Report(
                new UserNotification(
                    UserNotificationSeverity.Error,
                    "動画を開けませんでした。",
                    "別の動画を選択してください。"),
                "playback-open-unexpected-failure",
                exception.Message,
                exception,
                path);
            if (!hadCurrentVideo)
            {
                ShowEmptyState();
            }
        }
        finally
        {
            if (ReferenceEquals(_openCancellation, openCancellation))
            {
                _openCancellation = null;
            }
            if (!_closeRequested)
            {
                OpenVideoMenuItem.IsEnabled = true;
                UpdatePlaybackButton();
                UpdatePlaybackTimeline();
                UpdateVolumeControls();
                UpdatePlaybackRateControls();
            }
        }
    }

    private void PlayPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        TogglePlayPause();
    }

    internal async Task HandleLaunchRequestAsync(LaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        BringToForeground();
        if (request.FileArguments.Length != 1 || _closeRequested)
        {
            return;
        }

        if (_openTask is { } pendingOpen)
        {
            await pendingOpen;
        }

        await OpenVideoFromUserRequestAsync(request.FileArguments[0]);
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    private void TogglePlayPause()
    {
        if (_playbackBackend?.CurrentPath is null || _openTask is not null)
        {
            return;
        }

        if (_playbackBackend.IsPlaying)
        {
            _playbackBackend.Pause();
        }
        else
        {
            _playbackBackend.Play();
        }
    }

    private void PlaybackBackend_OnStateChanged(object? sender, EventArgs eventArgs)
    {
        if (!Dispatcher.CheckAccess())
        {
            try
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    UpdatePlaybackButton();
                    UpdatePlaybackTimeline();
                    UpdateVolumeControls();
                    UpdatePlaybackRateControls();
                });
            }
            catch (InvalidOperationException)
            {
                // The window Dispatcher is already shutting down.
            }

            return;
        }

        UpdatePlaybackButton();
        UpdatePlaybackTimeline();
        UpdateVolumeControls();
        UpdatePlaybackRateControls();
    }

    private void PlaybackBackend_OnErrorOccurred(object? sender, PlaybackErrorEventArgs eventArgs)
    {
        if (!Dispatcher.CheckAccess())
        {
            try
            {
                _ = Dispatcher.BeginInvoke(() => PlaybackBackend_OnErrorOccurred(sender, eventArgs));
            }
            catch (InvalidOperationException)
            {
                // The window Dispatcher is already shutting down.
            }

            return;
        }

        if (_disposed || _errorReporter is null)
        {
            return;
        }

        if (eventArgs.EventCode is "playback-native-error" or "playback-audio-output-error")
        {
            EndTemporaryPlaybackRateGesture();
            _hasPlaybackError = true;
            UpdateVolumeControls();
            UpdatePlaybackRateControls();
        }

        _errorReporter.Report(
            new UserNotification(
                UserNotificationSeverity.Error,
                eventArgs.UserMessage,
                eventArgs.SuggestedAction),
            eventArgs.EventCode,
            eventArgs.TechnicalMessage,
            eventArgs.Exception,
            eventArgs.TargetPath);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            if (!_closeRequested)
            {
                _closeRequested = true;
                EndTemporaryPlaybackRateGesture();
                OpenVideoMenuItem.IsEnabled = false;
                PlayPauseButton.IsEnabled = false;
                SeekSlider.IsEnabled = false;
                VolumeSlider.IsEnabled = false;
                MuteButton.IsEnabled = false;
                PlaybackRateMenuItem.IsEnabled = false;
                RecentFilesMenuItem.IsEnabled = false;
                PlaylistMenuItem.IsEnabled = false;
                _playlistWindow?.DisablePersistenceControls();
                VideoContextMenu.IsOpen = false;
                _openCancellation?.Cancel();
                _videoProfileSaveTimer.Stop();
                _ = CloseAfterPendingWorkCompletesAsync(
                    _openTask,
                    _recentFileMutationTask,
                    _playlistMutationTask);
            }

            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _disposed = true;
        if (_playlistWindow is not null)
        {
            _playlistWindow.LoopChanged -= PlaylistWindow_OnLoopChanged;
            _playlistWindow.EntriesAddRequested -= PlaylistWindow_OnEntriesAddRequested;
            _playlistWindow.EntriesRemoveRequested -= PlaylistWindow_OnEntriesRemoveRequested;
            _playlistWindow.EntryMoveRequested -= PlaylistWindow_OnEntryMoveRequested;
            _playlistWindow.Closed -= PlaylistWindow_OnClosed;
            _playlistWindow.Close();
            _playlistWindow = null;
        }
        _playbackTimelineTimer.Stop();
        _playbackTimelineTimer.Tick -= PlaybackTimelineTimer_OnTick;
        _videoProfileSaveTimer.Stop();
        _videoProfileSaveTimer.Tick -= VideoProfileSaveTimer_OnTick;
        _temporaryPlaybackRateTimer.Stop();
        _temporaryPlaybackRateTimer.Tick -= TemporaryPlaybackRateTimer_OnTick;
        _temporaryPlaybackRateReleaseTimer.Stop();
        _temporaryPlaybackRateReleaseTimer.Tick -= TemporaryPlaybackRateReleaseTimer_OnTick;
        _fullscreenControlsTimer.Stop();
        _fullscreenControlsTimer.Tick -= FullscreenControlsTimer_OnTick;
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        SeekSlider.RemoveHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(SeekSlider_OnDragStarted));
        SeekSlider.RemoveHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(SeekSlider_OnDragCompleted));
        if (_playbackBackend is not null)
        {
            _playbackBackend.ErrorOccurred -= PlaybackBackend_OnErrorOccurred;
            _playbackBackend.StateChanged -= PlaybackBackend_OnStateChanged;
        }

        VideoView.MediaPlayer = null;
        _playbackBackend?.Dispose();
        _playbackBackend = null;
        _videoProfiles?.Dispose();
        _videoProfiles = null;
        _recentFiles?.Dispose();
        _recentFiles = null;
        _playlist?.Dispose();
        _playlist = null;
        base.OnClosed(e);
    }

    private async Task RecordRecentFileAsync(string path)
    {
        var recentFiles = _recentFiles;
        if (recentFiles is null)
        {
            return;
        }

        try
        {
            var saveResult = await recentFiles.RecordSuccessfulOpenAsync(path);
            if (!_closeRequested)
            {
                RebuildRecentFilesMenu();
            }

            ReportRecentFileSaveFailure(saveResult, recentFiles.FilePath);
        }
        catch (Exception exception)
        {
            _errorReporter?.Report(
                new UserNotification(
                    UserNotificationSeverity.Warning,
                    "最近開いたファイルの履歴を保存できません。",
                    "再生は続行できます。アプリの配置先に書き込み権限があるか確認してください。"),
                "recent-files-save-unexpected-failure",
                exception.Message,
                exception,
                recentFiles.FilePath);
        }
    }

    private async Task RunRecentFileMutationAsync(
        Func<RecentFileRepository, Task<JsonSaveResult>> mutation)
    {
        var recentFiles = _recentFiles;
        if (recentFiles is null || _recentFileMutationTask is not null || _closeRequested)
        {
            return;
        }

        RecentFilesMenuItem.IsEnabled = false;
        Task<JsonSaveResult> mutationTask;
        try
        {
            mutationTask = mutation(recentFiles);
        }
        catch (Exception exception)
        {
            ReportRecentFileUnexpectedFailure(exception, recentFiles.FilePath);
            RebuildRecentFilesMenu();
            RecentFilesMenuItem.IsEnabled = true;
            return;
        }

        _recentFileMutationTask = mutationTask;
        try
        {
            var saveResult = await mutationTask;
            ReportRecentFileSaveFailure(saveResult, recentFiles.FilePath);
        }
        catch (Exception exception)
        {
            ReportRecentFileUnexpectedFailure(exception, recentFiles.FilePath);
        }
        finally
        {
            if (ReferenceEquals(_recentFileMutationTask, mutationTask))
            {
                _recentFileMutationTask = null;
            }

            if (!_closeRequested)
            {
                RebuildRecentFilesMenu();
                RecentFilesMenuItem.IsEnabled = true;
            }
        }
    }

    private void RebuildRecentFilesMenu()
    {
        RecentFilesMenuItem.Items.Clear();
        var presentation = RecentFileMenuPresentation.From(_recentFiles?.GetFiles() ?? []);
        if (presentation.Files.Count == 0)
        {
            RecentFilesMenuItem.Items.Add(new MenuItem
            {
                Header = "（履歴はありません）",
                IsEnabled = false,
            });
            return;
        }

        foreach (var file in presentation.Files)
        {
            RecentFilesMenuItem.Items.Add(new MenuItem
            {
                Header = file.IsMissing
                    ? $"{file.DisplayName}（見つかりません）"
                    : file.DisplayName,
                ToolTip = file.Path,
                Tag = new OpenRecentFileAction(file.Path),
                IsEnabled = !file.IsMissing && _playbackBackend is not null,
            });
        }

        if (presentation.MissingFiles.Count > 0)
        {
            RecentFilesMenuItem.Items.Add(new Separator());
            var removeMissingMenu = new MenuItem { Header = "欠損した項目を履歴から削除" };
            foreach (var file in presentation.MissingFiles)
            {
                removeMissingMenu.Items.Add(new MenuItem
                {
                    Header = file.DisplayName,
                    ToolTip = file.Path,
                    Tag = new RemoveRecentFileAction(file.Path),
                });
            }

            if (presentation.ShowRemoveAllMissing)
            {
                removeMissingMenu.Items.Add(new Separator());
                removeMissingMenu.Items.Add(new MenuItem
                {
                    Header = "すべて削除",
                    Tag = new RemoveAllMissingRecentFilesAction(),
                });
            }

            RecentFilesMenuItem.Items.Add(removeMissingMenu);
        }

        RecentFilesMenuItem.Items.Add(new Separator());
        RecentFilesMenuItem.Items.Add(new MenuItem
        {
            Header = "履歴をすべて消去",
            Tag = new ClearRecentFilesAction(),
        });
    }

    private void ReportRecentFileSaveFailure(JsonSaveResult saveResult, string targetPath)
    {
        if (saveResult.Success)
        {
            return;
        }

        _errorReporter?.Report(
            new UserNotification(
                UserNotificationSeverity.Warning,
                "最近開いたファイルの履歴を保存できません。",
                "再生は続行できます。アプリの配置先に書き込み権限があるか確認してください。"),
            "recent-files-save-failed",
            saveResult.ErrorMessage ?? "The recent-file history save failed.",
            saveResult.Exception,
            targetPath);
    }

    private void ReportRecentFileUnexpectedFailure(Exception exception, string targetPath) =>
        _errorReporter?.Report(
            new UserNotification(
                UserNotificationSeverity.Warning,
                "最近開いたファイルの履歴を保存できません。",
                "再生は続行できます。アプリの配置先に書き込み権限があるか確認してください。"),
            "recent-files-save-unexpected-failure",
            exception.Message,
            exception,
            targetPath);

    private void ReportPlaylistSaveFailure(Exception exception, string targetPath) =>
        _errorReporter?.Report(
            new UserNotification(
                UserNotificationSeverity.Warning,
                "プレイリストを保存できません。",
                "内容は現在の実行中だけ保持されます。アプリの配置先に書き込み権限があるか確認してください。"),
            "playlist-save-failed",
            exception.Message,
            exception,
            targetPath);

    private async Task CloseAfterPendingWorkCompletesAsync(
        Task? openTask,
        Task? recentFileMutationTask,
        Task? playlistMutationTask)
    {
        // OnClosing must return before Close is requested again when there is no pending work.
        await Dispatcher.Yield(DispatcherPriority.Background);

        await IgnoreReportedPendingFailureAsync(openTask);
        await IgnoreReportedPendingFailureAsync(recentFileMutationTask);
        await IgnoreReportedPendingFailureAsync(playlistMutationTask);

        try
        {
            CaptureCurrentVideoProfile(scheduleSave: false);
            await FlushVideoProfilesAsync();
        }
        catch (Exception exception)
        {
            ReportUnexpectedVideoProfileFailure(exception);
        }

        _allowClose = true;
        Close();
    }

    private static async Task IgnoreReportedPendingFailureAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // The operation owner reports the original failure before normal shutdown continues.
        }
    }

    private sealed record OpenRecentFileAction(string Path);

    private sealed record RemoveRecentFileAction(string Path);

    private sealed record RemoveAllMissingRecentFilesAction;

    private sealed record ClearRecentFilesAction;

    private void ShowEmptyState()
    {
        EmptyStateTitleText.Text = "動画が選択されていません";
        EmptyStateDescriptionText.Text = "「ファイル」→「開く」からmp4またはwmvを選択してください";
        EmptyStatePanel.Visibility = Visibility.Visible;
        Title = ApplicationInfo.DisplayName;
    }

    private void UpdatePlaybackButton()
    {
        if (_disposed)
        {
            return;
        }

        var presentation = PlaybackButtonPresentation.From(
            _playbackBackend?.CurrentPath is not null,
            _playbackBackend?.IsPlaying == true);
        PlayPauseButton.IsEnabled = presentation.IsEnabled && _openTask is null;
        PlayPauseButton.Content = presentation.Glyph;
        PlayPauseButton.ToolTip = presentation.ToolTip;
        AutomationProperties.SetName(PlayPauseButton, presentation.AccessibleName);
    }

    private void PlaybackTimelineTimer_OnTick(object? sender, EventArgs eventArgs) =>
        UpdatePlaybackTimeline();

    private void UpdatePlaybackTimeline()
    {
        if (_disposed)
        {
            return;
        }

        var backend = _playbackBackend;
        var presentation = PlaybackTimelinePresentation.From(
            backend?.CurrentPath is not null,
            _isOpeningVideo,
            backend?.IsSeekable == true,
            backend?.TimeMilliseconds ?? 0,
            backend?.LengthMilliseconds ?? 0);

        SeekSlider.IsEnabled = presentation.IsSeekEnabled;
        SeekSlider.ToolTip = presentation.SeekToolTip;
        UpdateStartPositionMarker();
        if (_isSeekDragging || DateTime.UtcNow < _seekPresentationHoldUntilUtc)
        {
            return;
        }

        _isUpdatingSeekSlider = true;
        try
        {
            SeekSlider.Value = presentation.NormalizedPosition;
        }
        finally
        {
            _isUpdatingSeekSlider = false;
        }

        TimeText.Text = presentation.TimeText;
    }

    private void UpdateVolumeControls()
    {
        if (_disposed)
        {
            return;
        }

        var backend = _playbackBackend;
        var presentation = PlaybackVolumePresentation.From(
            backend?.CurrentPath is not null,
            _isOpeningVideo || _openTask is not null,
            _hasPlaybackError,
            backend?.VolumePercent ?? PlaybackVolume.DefaultPercent,
            backend?.IsMuted == true);

        MuteButton.IsEnabled = presentation.IsEnabled;
        MuteButton.Content = presentation.MuteGlyph;
        MuteButton.ToolTip = presentation.MuteToolTip;
        AutomationProperties.SetName(MuteButton, presentation.MuteAccessibleName);
        VolumeSlider.IsEnabled = presentation.IsEnabled;
        VolumeSlider.ToolTip = presentation.VolumeToolTip;

        _isUpdatingVolumeSlider = true;
        try
        {
            VolumeSlider.Value = presentation.VolumePercent;
        }
        finally
        {
            _isUpdatingVolumeSlider = false;
        }

        VolumeText.Text = $"{presentation.VolumePercent}%";
        AutomationProperties.SetName(VolumeText, $"音量 {presentation.VolumePercent}%");
    }

    private void MuteButton_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var backend = _playbackBackend;
        if (!MuteButton.IsEnabled || backend is null)
        {
            return;
        }

        backend.SetMuted(!backend.IsMuted);
        UpdateVolumeControls();
        CaptureCurrentVideoProfile(scheduleSave: true);
    }

    private void VolumeSlider_OnValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        var backend = _playbackBackend;
        if (_isUpdatingVolumeSlider || !VolumeSlider.IsEnabled || backend is null)
        {
            return;
        }

        backend.SetVolumePercent((int)Math.Round(eventArgs.NewValue));
        UpdateVolumeControls();
        CaptureCurrentVideoProfile(scheduleSave: true);
    }

    private void VideoSurface_OnPreviewMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        var backend = _playbackBackend;
        if (eventArgs.Delta == 0 || !VolumeSlider.IsEnabled || backend is null)
        {
            return;
        }

        backend.SetVolumePercent(
            backend.VolumePercent + (Math.Sign(eventArgs.Delta) * PlaybackVolume.WheelStepPercent));
        UpdateVolumeControls();
        CaptureCurrentVideoProfile(scheduleSave: true);
        eventArgs.Handled = true;
    }

    private void VideoSurface_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.ClickCount >= 2)
        {
            EndTemporaryPlaybackRateGesture();
            if (CanControlPlayback() && !HasInputAncestor(eventArgs.OriginalSource as DependencyObject))
            {
                ToggleFullscreen();
                eventArgs.Handled = true;
            }

            return;
        }

        if (!CanControlPlayback() || HasInputAncestor(eventArgs.OriginalSource as DependencyObject))
        {
            return;
        }

        VideoInteractionSurface.Focus();
        var startPoint = eventArgs.GetPosition(VideoInteractionSurface);
        _temporaryPlaybackRateGesture.Begin(startPoint.X, startPoint.Y);
        _temporaryPlaybackRateTimer.Stop();
        _temporaryPlaybackRateTimer.Start();
        if (!Mouse.Capture(VideoInteractionSurface))
        {
            _temporaryPlaybackRateTimer.Stop();
            _temporaryPlaybackRateGesture.Cancel();
        }

        UpdatePlaybackRateControls();
    }

    private void VideoSurface_OnPreviewMouseMove(object sender, MouseEventArgs eventArgs)
    {
        ShowFullscreenControlsForActivity();
        var point = eventArgs.GetPosition(VideoInteractionSurface);
        if (_temporaryPlaybackRateGesture.CancelIfMoved(
                point.X,
                point.Y,
                SystemParameters.MinimumHorizontalDragDistance,
                SystemParameters.MinimumVerticalDragDistance))
        {
            EndTemporaryPlaybackRateGesture();
        }
    }

    private void Window_OnPreviewDragEnter(object sender, DragEventArgs eventArgs) =>
        UpdateDropFeedback(eventArgs);

    private void Window_OnPreviewDragOver(object sender, DragEventArgs eventArgs) =>
        UpdateDropFeedback(eventArgs);

    private void Window_OnPreviewDragLeave(object sender, DragEventArgs eventArgs)
    {
        DropTargetOverlay.Visibility = Visibility.Collapsed;
        eventArgs.Handled = true;
    }

    private async void Window_OnPreviewDrop(object sender, DragEventArgs eventArgs)
    {
        DropTargetOverlay.Visibility = Visibility.Collapsed;
        var request = ClassifyDrop(eventArgs.Data);
        eventArgs.Handled = true;
        EndTemporaryPlaybackRateGesture();

        switch (request.Kind)
        {
            case FileDropRequestKind.SingleSupportedFile:
                await OpenVideoFromUserRequestAsync(request.Path!);
                break;
            case FileDropRequestKind.MultipleFiles:
                ShowNotification(new UserNotification(
                    UserNotificationSeverity.Information,
                    "メインウィンドウでは1ファイルだけ指定してください。",
                    "動画を1ファイルだけ選び、もう一度ドロップしてください。"));
                break;
            case FileDropRequestKind.UnsupportedFile:
                ShowNotification(new UserNotification(
                    UserNotificationSeverity.Error,
                    "MP4またはWMVファイルを指定してください。",
                    "対応する動画ファイルを選び直してください。"));
                break;
        }
    }

    private void UpdateDropFeedback(DragEventArgs eventArgs)
    {
        var request = _openTask is null && !_closeRequested
            ? ClassifyDrop(eventArgs.Data)
            : new FileDropRequest(FileDropRequestKind.None);
        DropTargetOverlay.Visibility = request.Kind == FileDropRequestKind.None
            ? Visibility.Collapsed
            : Visibility.Visible;
        eventArgs.Effects = request.Kind == FileDropRequestKind.SingleSupportedFile
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        DropTargetText.Text = request.Kind switch
        {
            FileDropRequestKind.SingleSupportedFile => "動画をドロップして開く",
            FileDropRequestKind.MultipleFiles => "1ファイルだけ指定してください",
            FileDropRequestKind.UnsupportedFile => "MP4またはWMVを指定してください",
            _ => string.Empty,
        };
        eventArgs.Handled = true;
    }

    private static FileDropRequest ClassifyDrop(IDataObject data)
    {
        try
        {
            var paths = data.GetDataPresent(DataFormats.FileDrop)
                ? data.GetData(DataFormats.FileDrop) as string[] ?? []
                : [];
            return FileDropRequestClassifier.Classify(paths);
        }
        catch (Exception exception) when (
            exception is COMException or ExternalException or InvalidOperationException)
        {
            return new FileDropRequest(FileDropRequestKind.None);
        }
    }

    private void VideoSurface_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        var wasGestureInProgress =
            _temporaryPlaybackRateGesture.IsPending ||
            _temporaryPlaybackRateGesture.IsActive;
        EndTemporaryPlaybackRateGesture();
        eventArgs.Handled = wasGestureInProgress;
    }

    private void VideoSurface_OnLostMouseCapture(object sender, MouseEventArgs eventArgs)
    {
        if (_temporaryPlaybackRateGesture.IsActive)
        {
            return;
        }

        if (_temporaryPlaybackRateGesture.IsPending)
        {
            EndTemporaryPlaybackRateGesture();
        }
    }

    private void TemporaryPlaybackRateTimer_OnTick(object? sender, EventArgs eventArgs)
    {
        _temporaryPlaybackRateTimer.Stop();
        var backend = _playbackBackend;
        if (backend is null ||
            !_temporaryPlaybackRateGesture.TryActivate(
                Mouse.LeftButton == MouseButtonState.Pressed && CanControlPlayback(),
                backend.Rate))
        {
            EndTemporaryPlaybackRateGesture();
            return;
        }

        if (!backend.TrySetRate(2.0f))
        {
            _temporaryPlaybackRateGesture.Cancel();
            ReleaseVideoSurfaceMouseCapture();
            ReportPlaybackRateFailure(
                "playback-temporary-rate-change-rejected",
                "長押しの一時2.0倍速を開始できませんでした。");
        }
        else
        {
            _temporaryPlaybackRateReleaseTimer.Start();
        }

        UpdatePlaybackRateControls();
    }

    private void TemporaryPlaybackRateReleaseTimer_OnTick(object? sender, EventArgs eventArgs)
    {
        if (_temporaryPlaybackRateGesture.IsActive && Mouse.LeftButton == MouseButtonState.Released)
        {
            EndTemporaryPlaybackRateGesture();
        }
    }

    private void EndTemporaryPlaybackRateGesture()
    {
        _temporaryPlaybackRateTimer.Stop();
        _temporaryPlaybackRateReleaseTimer.Stop();
        var rateToRestore = _temporaryPlaybackRateGesture.End();
        var backend = _playbackBackend;
        if (rateToRestore is { } rate &&
            backend?.CurrentPath is not null &&
            !backend.TrySetRate(rate))
        {
            ReportPlaybackRateFailure(
                "playback-temporary-rate-restore-rejected",
                "長押し前の再生速度へ戻せませんでした。");
        }

        ReleaseVideoSurfaceMouseCapture();
        UpdatePlaybackRateControls();
    }

    private void ReleaseVideoSurfaceMouseCapture()
    {
        if (Mouse.Captured == VideoInteractionSurface)
        {
            Mouse.Capture(null);
        }
    }

    private nint WindowMessageHook(
        nint windowHandle,
        int message,
        nint wordParameter,
        nint longParameter,
        ref bool handled)
    {
        if (message == WindowMessageActivateApplication && wordParameter == 0)
        {
            EndTemporaryPlaybackRateGesture();
        }

        return 0;
    }

    private static bool HasInputAncestor(DependencyObject? source)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is ButtonBase or Slider or ComboBox or TextBoxBase or PasswordBox or MenuItem)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject current) =>
        current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
            ? System.Windows.Media.VisualTreeHelper.GetParent(current)
            : LogicalTreeHelper.GetParent(current);

    private static bool IsShortcutInputFocused() =>
        Keyboard.FocusedElement is DependencyObject focusedElement && HasInputAncestor(focusedElement);

    private void VideoContextMenu_OnOpened(object sender, RoutedEventArgs eventArgs)
    {
        UpdateFullscreenControlsInteraction(isContextMenuOpen: true);
        UpdatePlaybackRateControls();
        SetStartPositionMenuItem.IsEnabled =
            CanControlPlayback() &&
            _videoProfiles is not null &&
            _playbackBackend is { IsSeekable: true, LengthMilliseconds: > 0 };
        FullscreenMenuItem.IsChecked = _isFullscreen;
    }

    private async void SetStartPositionMenuItem_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var backend = _playbackBackend;
        var profiles = _videoProfiles;
        if (!SetStartPositionMenuItem.IsEnabled ||
            backend?.CurrentPath is null ||
            profiles is null ||
            backend.LengthMilliseconds <= 0)
        {
            return;
        }

        eventArgs.Handled = true;
        var path = backend.CurrentPath;
        var positionMilliseconds = Math.Clamp(
            backend.TimeMilliseconds,
            0,
            backend.LengthMilliseconds);
        try
        {
            profiles.SetStartPosition(path, positionMilliseconds);
            UpdateStartPositionMarker();
            _videoProfileRevision++;
            _videoProfileSaveTimer.Stop();
            if (await FlushVideoProfilesAsync())
            {
                ShowNotification(new UserNotification(
                    UserNotificationSeverity.Information,
                    $"再生開始位置を {PlaybackTimelinePresentation.FormatMilliseconds(positionMilliseconds)} に設定しました。",
                    "次回からこの位置で再生します。"));
            }
        }
        catch (Exception exception)
        {
            ReportUnexpectedVideoProfileFailure(exception);
        }
    }

    private void VideoContextMenu_OnClosed(object sender, RoutedEventArgs eventArgs) =>
        UpdateFullscreenControlsInteraction(isContextMenuOpen: false);

    private void Window_OnPreviewMouseMove(object sender, MouseEventArgs eventArgs) =>
        ShowFullscreenControlsForActivity();

    private void PlaybackControls_OnMouseEnter(object sender, MouseEventArgs eventArgs) =>
        UpdateFullscreenControlsInteraction(isPointerOverControls: true);

    private void PlaybackControls_OnMouseLeave(object sender, MouseEventArgs eventArgs) =>
        UpdateFullscreenControlsInteraction(isPointerOverControls: false);

    private void FullscreenMenuItem_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        ToggleFullscreen();
        FullscreenMenuItem.IsChecked = _isFullscreen;
        eventArgs.Handled = true;
    }

    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        var key = eventArgs.Key == Key.System ? eventArgs.SystemKey : eventArgs.Key;
        var action = PlaybackShortcutMap.Resolve(key, Keyboard.Modifiers, _isFullscreen);
        if (action == PlaybackShortcutAction.None)
        {
            return;
        }

        if (action == PlaybackShortcutAction.ToggleFullscreen)
        {
            ToggleFullscreen();
            eventArgs.Handled = true;
            return;
        }

        if (action == PlaybackShortcutAction.ExitFullscreen)
        {
            ExitFullscreen();
            eventArgs.Handled = true;
            return;
        }

        if (IsShortcutInputFocused() || !CanControlPlayback())
        {
            return;
        }

        switch (action)
        {
            case PlaybackShortcutAction.TogglePlayPause:
                TogglePlayPause();
                break;
            case PlaybackShortcutAction.SeekBackward:
                SeekByShortcut(-PlaybackShortcutMap.SeekStep);
                break;
            case PlaybackShortcutAction.SeekForward:
                SeekByShortcut(PlaybackShortcutMap.SeekStep);
                break;
            case PlaybackShortcutAction.IncreasePlaybackRate:
                StepPlaybackRate(1);
                break;
            case PlaybackShortcutAction.DecreasePlaybackRate:
                StepPlaybackRate(-1);
                break;
        }

        eventArgs.Handled = true;
    }

    private void SeekByShortcut(TimeSpan offset)
    {
        var backend = _playbackBackend;
        if (backend is null || !backend.IsSeekable || backend.LengthMilliseconds <= 0)
        {
            return;
        }

        var normalizedPosition = PlaybackPosition.OffsetByMilliseconds(
            backend.TimeMilliseconds,
            backend.LengthMilliseconds,
            (long)offset.TotalMilliseconds);
        backend.Seek(normalizedPosition);
        PresentRequestedSeek(normalizedPosition, backend.LengthMilliseconds);
    }

    private void PresentRequestedSeek(double normalizedPosition, long lengthMilliseconds)
    {
        _seekPresentationHoldUntilUtc = DateTime.UtcNow + PlaybackTimelineRefreshInterval;
        _isUpdatingSeekSlider = true;
        try
        {
            SeekSlider.Value = normalizedPosition;
        }
        finally
        {
            _isUpdatingSeekSlider = false;
        }

        var previewMilliseconds = (long)(normalizedPosition * lengthMilliseconds);
        TimeText.Text =
            $"{PlaybackTimelinePresentation.FormatMilliseconds(previewMilliseconds)} / " +
            PlaybackTimelinePresentation.FormatMilliseconds(lengthMilliseconds);
    }

    private void StepPlaybackRate(int direction)
    {
        EndTemporaryPlaybackRateGesture();
        var backend = _playbackBackend;
        if (backend is null)
        {
            return;
        }

        var targetRate = PlaybackRate.Step(backend.Rate, direction);
        if (!backend.TrySetRate(targetRate))
        {
            ReportPlaybackRateFailure(
                "playback-shortcut-rate-change-rejected",
                "再生速度を変更できませんでした。",
                $"The playback backend rejected shortcut rate {targetRate}.");
        }

        UpdatePlaybackRateControls();
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
        }
        else
        {
            EnterFullscreen();
        }
    }

    private void EnterFullscreen()
    {
        if (_isFullscreen)
        {
            return;
        }

        EndTemporaryPlaybackRateGesture();
        _windowStateBeforeFullscreen = WindowState;
        _windowStyleBeforeFullscreen = WindowStyle;
        _resizeModeBeforeFullscreen = ResizeMode;

        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        MainMenu.Visibility = Visibility.Collapsed;
        Grid.SetRow(VideoSurface, 0);
        Grid.SetRowSpan(VideoSurface, 4);
        MovePlaybackControlsToFullscreenOverlay();
        PlaybackControls.Opacity = 0.94;
        WindowState = WindowState.Maximized;
        _isFullscreen = true;
        _fullscreenControlsState.EnterFullscreen();
        _fullscreenControlsState.SetPointerOverControls(PlaybackControls.IsMouseOver);
        _fullscreenControlsState.SetContextMenuOpen(VideoContextMenu.IsOpen);
        ShowFullscreenControlsForActivity();
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen)
        {
            return;
        }

        EndTemporaryPlaybackRateGesture();
        _fullscreenControlsTimer.Stop();
        _fullscreenControlsState.ExitFullscreen();
        PlaybackControls.Visibility = Visibility.Visible;
        WindowState = WindowState.Normal;
        WindowStyle = _windowStyleBeforeFullscreen;
        ResizeMode = _resizeModeBeforeFullscreen;
        Grid.SetRow(VideoSurface, 1);
        Grid.SetRowSpan(VideoSurface, 1);
        MovePlaybackControlsToNormalLayout();
        PlaybackControls.Opacity = 1;
        MainMenu.Visibility = Visibility.Visible;
        _isFullscreen = false;
        WindowState = _windowStateBeforeFullscreen;
    }

    private void MovePlaybackControlsToFullscreenOverlay()
    {
        if (!RootLayout.Children.Contains(PlaybackControls))
        {
            return;
        }

        RootLayout.Children.Remove(PlaybackControls);
        VideoInteractionSurface.Children.Add(PlaybackControls);
        PlaybackControls.VerticalAlignment = VerticalAlignment.Bottom;
        Panel.SetZIndex(PlaybackControls, 1);
    }

    private void MovePlaybackControlsToNormalLayout()
    {
        if (!VideoInteractionSurface.Children.Contains(PlaybackControls))
        {
            return;
        }

        VideoInteractionSurface.Children.Remove(PlaybackControls);
        RootLayout.Children.Add(PlaybackControls);
        Grid.SetRow(PlaybackControls, 2);
        PlaybackControls.VerticalAlignment = VerticalAlignment.Stretch;
        Panel.SetZIndex(PlaybackControls, 0);
    }

    private void ShowFullscreenControlsForActivity()
    {
        if (!_fullscreenControlsState.ShowForActivity())
        {
            return;
        }

        PlaybackControls.Visibility = Visibility.Visible;
        RestartFullscreenControlsTimerIfIdle();
    }

    private void UpdateFullscreenControlsInteraction(
        bool? isPointerOverControls = null,
        bool? isContextMenuOpen = null)
    {
        var isFullscreen = isPointerOverControls is { } pointerState
            ? _fullscreenControlsState.SetPointerOverControls(pointerState)
            : _fullscreenControlsState.SetContextMenuOpen(isContextMenuOpen ?? false);
        if (!isFullscreen)
        {
            return;
        }

        PlaybackControls.Visibility = Visibility.Visible;
        RestartFullscreenControlsTimerIfIdle();
    }

    private void RestartFullscreenControlsTimerIfIdle()
    {
        _fullscreenControlsTimer.Stop();
        if (!_fullscreenControlsState.IsInteractionActive)
        {
            _fullscreenControlsTimer.Start();
        }
    }

    private void FullscreenControlsTimer_OnTick(object? sender, EventArgs eventArgs)
    {
        _fullscreenControlsTimer.Stop();
        _fullscreenControlsState.SetPointerOverControls(PlaybackControls.IsMouseOver);
        _fullscreenControlsState.SetContextMenuOpen(VideoContextMenu.IsOpen);
        if (_fullscreenControlsState.IsInteractionActive)
        {
            return;
        }

        if (_fullscreenControlsState.TryHideAfterTimeout())
        {
            PlaybackControls.Visibility = Visibility.Collapsed;
        }
    }

    private void PlaybackRateMenuItem_OnClick(object sender, RoutedEventArgs eventArgs)
    {
        var backend = _playbackBackend;
        if (sender is not MenuItem { Tag: string rateText } ||
            !PlaybackRateMenuItem.IsEnabled ||
            backend is null ||
            !PlaybackRate.TryParse(rateText, out var rate))
        {
            return;
        }

        if (!backend.TrySetRate(rate))
        {
            ReportPlaybackRateFailure(
                "playback-rate-change-rejected",
                "再生速度を変更できませんでした。",
                $"The playback backend rejected rate {rateText}.");
        }

        UpdatePlaybackRateControls();
        eventArgs.Handled = true;
    }

    private void UpdatePlaybackRateControls()
    {
        if (_disposed)
        {
            return;
        }

        var backend = _playbackBackend;
        var currentRate = backend?.Rate ?? PlaybackRate.Default;
        var formattedRate = PlaybackRate.Format(currentRate);
        PlaybackRateText.Text = formattedRate;
        AutomationProperties.SetName(PlaybackRateText, $"現在の再生速度 {formattedRate}");
        PlaybackRateMenuItem.IsEnabled =
            CanControlPlayback() &&
            !_temporaryPlaybackRateGesture.IsPending &&
            !_temporaryPlaybackRateGesture.IsActive;

        foreach (var item in PlaybackRateMenuItem.Items.OfType<MenuItem>())
        {
            item.IsChecked =
                item.Tag is string rateText &&
                PlaybackRate.TryParse(rateText, out var itemRate) &&
                PlaybackRate.AreEqual(itemRate, currentRate);
        }
    }

    private bool CanControlPlayback() =>
        _playbackBackend?.CurrentPath is not null &&
        _openTask is null &&
        !_isOpeningVideo &&
        !_hasPlaybackError &&
        !_closeRequested;

    private void ReportPlaybackRateFailure(
        string eventCode,
        string message,
        string? technicalMessage = null)
    {
        _errorReporter?.Report(
            new UserNotification(
                UserNotificationSeverity.Warning,
                message,
                "動画を開き直して、もう一度お試しください。"),
            eventCode,
            technicalMessage ?? message,
            targetPath: _playbackBackend?.CurrentPath);
    }

    private PlaybackInitialState? GetInitialPlaybackState(string path)
    {
        var profiles = _videoProfiles;
        if (profiles is null)
        {
            return null;
        }

        try
        {
            return profiles.TryGet(path, out var profile)
                ? new PlaybackInitialState(
                    new PlaybackAudioState(profile.VolumePercent, profile.IsMuted),
                    profile.StartPositionMilliseconds)
                : new PlaybackInitialState(PlaybackAudioState.Default, startPositionMilliseconds: null);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Let the playback backend report the invalid path through its structured error path.
            return null;
        }
    }

    private void CaptureCurrentVideoProfile(bool scheduleSave)
    {
        var backend = _playbackBackend;
        var profiles = _videoProfiles;
        if (backend?.CurrentPath is null || profiles is null)
        {
            return;
        }

        profiles.Set(backend.CurrentPath, backend.VolumePercent, backend.IsMuted);
        _videoProfileRevision++;
        if (scheduleSave)
        {
            _videoProfileSaveTimer.Stop();
            _videoProfileSaveTimer.Start();
        }
    }

    private async void VideoProfileSaveTimer_OnTick(object? sender, EventArgs eventArgs)
    {
        _videoProfileSaveTimer.Stop();
        try
        {
            await FlushVideoProfilesAsync();
        }
        catch (Exception exception)
        {
            ReportUnexpectedVideoProfileFailure(exception);
        }
    }

    private async Task<bool> FlushVideoProfilesAsync()
    {
        var profiles = _videoProfiles;
        if (profiles is null || _savedVideoProfileRevision >= _videoProfileRevision)
        {
            return true;
        }

        if (_videoProfileSaveTask is { IsCompleted: false } pendingSave)
        {
            await pendingSave;
            if (_savedVideoProfileRevision >= _videoProfileRevision)
            {
                return true;
            }
        }

        var revision = _videoProfileRevision;
        var saveTask = profiles.SaveAsync();
        _videoProfileSaveTask = saveTask;
        JsonSaveResult saveResult;
        try
        {
            saveResult = await saveTask;
        }
        finally
        {
            if (ReferenceEquals(_videoProfileSaveTask, saveTask))
            {
                _videoProfileSaveTask = null;
            }
        }

        if (saveResult.Success)
        {
            _savedVideoProfileRevision = Math.Max(_savedVideoProfileRevision, revision);
            return true;
        }

        _errorReporter?.Report(
            new UserNotification(
                UserNotificationSeverity.Warning,
                "動画ごとの設定を保存できません。",
                "再生は続行できます。アプリの配置先に書き込み権限があるか確認してください。"),
            "video-profiles-save-failed",
            saveResult.ErrorMessage ?? "The video profile save failed.",
            saveResult.Exception,
            profiles.FilePath);
        return false;
    }

    private void ReportUnexpectedVideoProfileFailure(Exception exception)
    {
        _errorReporter?.Report(
            new UserNotification(
                UserNotificationSeverity.Warning,
                "動画ごとの設定を保存できません。",
                "再生は続行できます。アプリの配置先に書き込み権限があるか確認してください。"),
            "video-profiles-save-unexpected-failure",
            exception.Message,
            exception,
            _videoProfiles?.FilePath);
    }

    private void SeekSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        var backend = _playbackBackend;
        if (_isUpdatingSeekSlider ||
            !SeekSlider.IsEnabled ||
            _isOpeningVideo ||
            backend?.CurrentPath is null)
        {
            return;
        }

        var normalizedPosition = PlaybackPosition.Normalize(eventArgs.NewValue);
        backend.Seek(normalizedPosition);
        var lengthMilliseconds = backend.LengthMilliseconds;
        if (lengthMilliseconds > 0)
        {
            PresentRequestedSeek(normalizedPosition, lengthMilliseconds);
        }
    }

    private void SeekSlider_OnDragStarted(object sender, DragStartedEventArgs eventArgs) =>
        _isSeekDragging = true;

    private void SeekSlider_OnDragCompleted(object sender, DragCompletedEventArgs eventArgs)
    {
        _isSeekDragging = false;
    }

    private void SeekSlider_OnSizeChanged(object sender, SizeChangedEventArgs eventArgs) =>
        UpdateStartPositionMarker();

    private void UpdateStartPositionMarker()
    {
        var backend = _playbackBackend;
        var profiles = _videoProfiles;
        var durationMilliseconds = backend?.LengthMilliseconds ?? 0;
        if (backend?.CurrentPath is not { } path ||
            profiles is null ||
            durationMilliseconds <= 0 ||
            !profiles.TryGet(path, out var profile) ||
            profile.StartPositionMilliseconds is not { } startPositionMilliseconds ||
            startPositionMilliseconds > durationMilliseconds ||
            SeekSlider.ActualWidth <= 0)
        {
            StartPositionMarker.Visibility = Visibility.Collapsed;
            return;
        }

        Canvas.SetLeft(
            StartPositionMarker,
            SeekUiGeometry.MarkerOffset(
                startPositionMilliseconds,
                durationMilliseconds,
                SeekSlider.ActualWidth,
                StartPositionMarker.Width));
        StartPositionMarker.Visibility = Visibility.Visible;
    }

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e) => Close();

    private void DismissNotificationButton_OnClick(object sender, RoutedEventArgs e) =>
        NotificationBorder.Visibility = Visibility.Collapsed;
}

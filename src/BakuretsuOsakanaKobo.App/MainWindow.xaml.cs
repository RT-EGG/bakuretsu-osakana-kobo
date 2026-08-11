using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using BakuretsuOsakanaKobo.Infrastructure.Errors;
using BakuretsuOsakanaKobo.Infrastructure.Persistence;
using BakuretsuOsakanaKobo.Playback;
using Microsoft.Win32;

namespace BakuretsuOsakanaKobo;

public partial class MainWindow : Window
{
    private static readonly TimeSpan PlaybackTimelineRefreshInterval = TimeSpan.FromMilliseconds(200);
    internal static readonly TimeSpan VideoProfileSaveDelay = TimeSpan.FromSeconds(3);

    private readonly DispatcherTimer _playbackTimelineTimer;
    private readonly DispatcherTimer _videoProfileSaveTimer;
    private PortableDataPaths? _paths;
    private ErrorReporter? _errorReporter;
    private IPlaybackBackend? _playbackBackend;
    private VideoProfileRepository? _videoProfiles;
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
    private DateTime _seekPresentationHoldUntilUtc;
    private bool _closeRequested;
    private bool _allowClose;
    private bool _disposed;

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
        SeekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(SeekSlider_OnDragStarted));
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(SeekSlider_OnDragCompleted));
    }

    internal void ConfigureServices(
        PortableDataPaths paths,
        ErrorReporter errorReporter,
        IPlaybackBackend? playbackBackend,
        VideoProfileRepository? videoProfiles = null)
    {
        _paths = paths;
        _errorReporter = errorReporter;
        _playbackBackend = playbackBackend;
        _videoProfiles = videoProfiles;
        OpenVideoMenuItem.IsEnabled = playbackBackend is not null;

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
        NotificationMessageText.Text = notification.Message;
        NotificationActionText.Text = notification.SuggestedAction;
        NotificationBorder.Visibility = Visibility.Visible;
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

        _isOpeningVideo = true;
        UpdatePlaybackButton();
        UpdatePlaybackTimeline();
        UpdateVolumeControls();
        UpdatePlaybackRateControls();
        _openTask = OpenVideoAsync(dialog.FileName);
        try
        {
            await _openTask;
        }
        finally
        {
            _openTask = null;
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
            var initialAudioState = GetInitialAudioState(path);
            if (await _playbackBackend.OpenAndPlayAsync(
                    path,
                    initialAudioState,
                    openCancellation.Token))
            {
                _hasPlaybackError = false;
                EmptyStatePanel.Visibility = Visibility.Collapsed;
                Title = $"{Path.GetFileName(path)} - {ApplicationInfo.DisplayName}";
                NotificationBorder.Visibility = Visibility.Collapsed;
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
                OpenVideoMenuItem.IsEnabled = false;
                PlayPauseButton.IsEnabled = false;
                SeekSlider.IsEnabled = false;
                VolumeSlider.IsEnabled = false;
                MuteButton.IsEnabled = false;
                PlaybackRateMenuItem.IsEnabled = false;
                VideoContextMenu.IsOpen = false;
                _openCancellation?.Cancel();
                _videoProfileSaveTimer.Stop();
                _ = CloseAfterPendingWorkCompletesAsync(_openTask);
            }

            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _disposed = true;
        _playbackTimelineTimer.Stop();
        _playbackTimelineTimer.Tick -= PlaybackTimelineTimer_OnTick;
        _videoProfileSaveTimer.Stop();
        _videoProfileSaveTimer.Tick -= VideoProfileSaveTimer_OnTick;
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
        base.OnClosed(e);
    }

    private async Task CloseAfterPendingWorkCompletesAsync(Task? openTask)
    {
        // OnClosing must return before Close is requested again when there is no pending work.
        await Dispatcher.Yield(DispatcherPriority.Background);

        try
        {
            if (openTask is not null)
            {
                await openTask;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // OpenVideoAsync reports the original failure before the window closes.
        }

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

    private void VideoContextMenu_OnOpened(object sender, RoutedEventArgs eventArgs) =>
        UpdatePlaybackRateControls();

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
            _errorReporter?.Report(
                new UserNotification(
                    UserNotificationSeverity.Warning,
                    "再生速度を変更できませんでした。",
                    "動画を開き直して、もう一度お試しください。"),
                "playback-rate-change-rejected",
                $"The playback backend rejected rate {rateText}.",
                targetPath: backend.CurrentPath);
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
            backend?.CurrentPath is not null &&
            _openTask is null &&
            !_isOpeningVideo &&
            !_hasPlaybackError &&
            !_closeRequested;

        foreach (var item in PlaybackRateMenuItem.Items.OfType<MenuItem>())
        {
            item.IsChecked =
                item.Tag is string rateText &&
                PlaybackRate.TryParse(rateText, out var itemRate) &&
                PlaybackRate.AreEqual(itemRate, currentRate);
        }
    }

    private PlaybackAudioState? GetInitialAudioState(string path)
    {
        var profiles = _videoProfiles;
        if (profiles is null)
        {
            return null;
        }

        try
        {
            return profiles.TryGet(path, out var profile)
                ? new PlaybackAudioState(profile.VolumePercent, profile.IsMuted)
                : PlaybackAudioState.Default;
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

    private async Task FlushVideoProfilesAsync()
    {
        var profiles = _videoProfiles;
        if (profiles is null || _savedVideoProfileRevision >= _videoProfileRevision)
        {
            return;
        }

        if (_videoProfileSaveTask is { IsCompleted: false } pendingSave)
        {
            await pendingSave;
            if (_savedVideoProfileRevision >= _videoProfileRevision)
            {
                return;
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
            return;
        }

        _errorReporter?.Report(
            new UserNotification(
                UserNotificationSeverity.Warning,
                "動画ごとの音量設定を保存できません。",
                "再生は続行できます。アプリの配置先に書き込み権限があるか確認してください。"),
            "video-profiles-save-failed",
            saveResult.ErrorMessage ?? "The video profile save failed.",
            saveResult.Exception,
            profiles.FilePath);
    }

    private void ReportUnexpectedVideoProfileFailure(Exception exception)
    {
        _errorReporter?.Report(
            new UserNotification(
                UserNotificationSeverity.Warning,
                "動画ごとの音量設定を保存できません。",
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
        _seekPresentationHoldUntilUtc = DateTime.UtcNow + PlaybackTimelineRefreshInterval;

        var lengthMilliseconds = backend.LengthMilliseconds;
        if (lengthMilliseconds > 0)
        {
            var previewMilliseconds = (long)(normalizedPosition * lengthMilliseconds);
            TimeText.Text =
                $"{PlaybackTimelinePresentation.FormatMilliseconds(previewMilliseconds)} / " +
                PlaybackTimelinePresentation.FormatMilliseconds(lengthMilliseconds);
        }
    }

    private void SeekSlider_OnDragStarted(object sender, DragStartedEventArgs eventArgs) =>
        _isSeekDragging = true;

    private void SeekSlider_OnDragCompleted(object sender, DragCompletedEventArgs eventArgs)
    {
        _isSeekDragging = false;
    }

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e) => Close();

    private void DismissNotificationButton_OnClick(object sender, RoutedEventArgs e) =>
        NotificationBorder.Visibility = Visibility.Collapsed;
}

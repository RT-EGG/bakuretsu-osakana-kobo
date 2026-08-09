using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
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

    private readonly DispatcherTimer _playbackTimelineTimer;
    private PortableDataPaths? _paths;
    private ErrorReporter? _errorReporter;
    private IPlaybackBackend? _playbackBackend;
    private CancellationTokenSource? _openCancellation;
    private Task? _openTask;
    private bool _isOpeningVideo;
    private bool _isUpdatingSeekSlider;
    private bool _isUpdatingVolumeSlider;
    private bool _isSeekDragging;
    private bool _hasPlaybackError;
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
        SeekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(SeekSlider_OnDragStarted));
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(SeekSlider_OnDragCompleted));
    }

    internal void ConfigureServices(
        PortableDataPaths paths,
        ErrorReporter errorReporter,
        IPlaybackBackend? playbackBackend)
    {
        _paths = paths;
        _errorReporter = errorReporter;
        _playbackBackend = playbackBackend;
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
            if (await _playbackBackend.OpenAndPlayAsync(path, openCancellation.Token))
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

        if (eventArgs.EventCode == "playback-native-error")
        {
            _hasPlaybackError = true;
            UpdateVolumeControls();
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
        if (!_allowClose && _openTask is { IsCompleted: false } openTask)
        {
            e.Cancel = true;
            if (!_closeRequested)
            {
                _closeRequested = true;
                OpenVideoMenuItem.IsEnabled = false;
                _openCancellation?.Cancel();
                _ = CloseAfterOpenCompletesAsync(openTask);
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
        base.OnClosed(e);
    }

    private async Task CloseAfterOpenCompletesAsync(Task openTask)
    {
        try
        {
            await openTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // OpenVideoAsync reports the original failure before the window closes.
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
        eventArgs.Handled = true;
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

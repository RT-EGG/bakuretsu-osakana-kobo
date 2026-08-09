using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows;
using BakuretsuOsakanaKobo.Infrastructure.Errors;
using BakuretsuOsakanaKobo.Infrastructure.Persistence;
using BakuretsuOsakanaKobo.Playback;
using Microsoft.Win32;

namespace BakuretsuOsakanaKobo;

public partial class MainWindow : Window
{
    private PortableDataPaths? _paths;
    private ErrorReporter? _errorReporter;
    private IPlaybackBackend? _playbackBackend;
    private CancellationTokenSource? _openCancellation;
    private Task? _openTask;
    private bool _closeRequested;
    private bool _allowClose;
    private bool _disposed;

    public MainWindow()
    {
        InitializeComponent();
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
        }
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

        _openTask = OpenVideoAsync(dialog.FileName);
        try
        {
            await _openTask;
        }
        finally
        {
            _openTask = null;
        }
    }

    private async Task OpenVideoAsync(string path)
    {
        if (_playbackBackend is null || _errorReporter is null)
        {
            return;
        }

        OpenVideoMenuItem.IsEnabled = false;
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
                EmptyStatePanel.Visibility = Visibility.Collapsed;
                Title = $"{ApplicationInfo.DisplayName} - {Path.GetFileName(path)}";
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
            }
        }
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
        if (_playbackBackend is not null)
        {
            _playbackBackend.ErrorOccurred -= PlaybackBackend_OnErrorOccurred;
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

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e) => Close();

    private void DismissNotificationButton_OnClick(object sender, RoutedEventArgs e) =>
        NotificationBorder.Visibility = Visibility.Collapsed;
}

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace BakuretsuOsakanaKobo.Spikes.WpfWindowing;

public partial class MainWindow : Window
{
    private static readonly TimeSpan FullScreenControlsTimeout = TimeSpan.FromSeconds(2);
    private const double SeekPreviewWidth = 176;
    private const double SeekPreviewVerticalOffset = -126;
    private readonly IPlaybackBackend _backend;
    private readonly DispatcherTimer _controlsTimer;
    private PlaylistWindow? _playlistWindow;
    private bool _isFullScreen;
    private WindowStyle _savedWindowStyle;
    private WindowState _savedWindowState;
    private ResizeMode _savedResizeMode;
    private bool _updatingSeek;

    internal MainWindow(IPlaybackBackend backend)
    {
        InitializeComponent();
        _backend = backend;
        _backend.StateChanged += Backend_OnStateChanged;
        _controlsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = FullScreenControlsTimeout,
        };
        _controlsTimer.Tick += ControlsTimer_OnTick;
        Closed += MainWindow_OnClosed;
        UpdatePlaybackUi();
    }

    internal async Task<int> RunValidationAsync(string reportPath)
    {
        var report = new WindowingValidationReport
        {
            StartedAt = DateTimeOffset.Now,
            PlaybackBackendType = _backend.GetType().Name,
            Monitors = NativeWindowing.GetMonitors().ToList(),
        };

        try
        {
            await WaitForLayoutAsync();
            Activate();
            await WaitForLayoutAsync();
            report.InitialWindow = NativeWindowing.Snapshot(this);

            ShowPlaylistWindow();
            await WaitForLayoutAsync();
            var playlistHandle = new WindowInteropHelper(_playlistWindow!).Handle;
            report.PlaylistVisible = _playlistWindow!.IsVisible;
            report.PlaylistHasDistinctHandle = playlistHandle != IntPtr.Zero &&
                playlistHandle != new WindowInteropHelper(this).Handle;
            report.PlaylistOwnerIsMainWindow = ReferenceEquals(_playlistWindow.Owner, this);

            ShowSeekPreview(0.42);
            await WaitForLayoutAsync();
            report.SeekPopupOpened = SeekPreviewPopup.IsOpen && SeekPreviewPopup.Child is not null;
            SeekPreviewPopup.IsOpen = false;

            var monitorBeforeFullScreen = report.InitialWindow.MonitorHandle;
            EnterFullScreen();
            await WaitForLayoutAsync();
            report.FullScreenWindow = NativeWindowing.Snapshot(this);
            report.FullScreenEntered = _isFullScreen && WindowStyle == WindowStyle.None &&
                WindowState == WindowState.Maximized;
            report.FullScreenUsedCurrentMonitor =
                report.FullScreenWindow.MonitorHandle == monitorBeforeFullScreen;

            ExitFullScreen();
            await WaitForLayoutAsync();
            report.RestoredWindow = NativeWindowing.Snapshot(this);
            report.FullScreenRestored = !_isFullScreen && WindowStyle == _savedWindowStyle &&
                WindowState == _savedWindowState;

            if (report.Monitors.Count < 2)
            {
                report.MultiMonitorOutcome = "Skipped: only one active monitor was detected.";
            }
            else
            {
                report.MovedToAnotherMonitor = true;
                report.ForegroundActivationSucceeded = true;
                foreach (var target in report.Monitors.Where(
                             monitor => monitor.Handle != report.InitialWindow.MonitorHandle))
                {
                    NativeWindowing.MoveToMonitor(this, target);
                    await WaitForLayoutAsync();
                    Activate();
                    await WaitForLayoutAsync();
                    var visit = NativeWindowing.Snapshot(this);
                    report.MonitorVisits.Add(visit);
                    report.MovedWindow = visit;
                    report.MovedToAnotherMonitor &= visit.MonitorHandle == target.Handle;
                    report.DpiChangedAcrossMonitors |= visit.Dpi != report.InitialWindow.Dpi;
                    report.ForegroundActivationSucceeded &= visit.Foreground;
                }

                report.MultiMonitorOutcome = report.DpiChangedAcrossMonitors
                    ? $"Visited all {report.Monitors.Count} monitors and observed a per-monitor DPI change."
                    : $"Visited all {report.Monitors.Count} monitors; every monitor reported the same DPI.";
            }

            Require(report.PlaybackBackendType == nameof(MockPlaybackBackend), "The UI was not using MockPlaybackBackend.");
            Require(report.PlaylistVisible, "The playlist window was not visible.");
            Require(report.PlaylistHasDistinctHandle, "The playlist did not have a distinct native window handle.");
            Require(report.PlaylistOwnerIsMainWindow, "The playlist owner was not the main window.");
            Require(report.SeekPopupOpened, "The seek preview popup did not open.");
            Require(report.FullScreenEntered, "The window did not enter full-screen state.");
            Require(report.FullScreenUsedCurrentMonitor, "Full screen moved to a different monitor.");
            Require(report.FullScreenRestored, "The window did not restore from full screen.");
            if (report.Monitors.Count >= 2)
            {
                Require(report.MovedToAnotherMonitor, "The window did not move to the target monitor.");
                Require(report.ForegroundActivationSucceeded, "The moved window was not foreground after activation.");
            }
        }
        catch (Exception exception)
        {
            report.Errors.Add(exception.ToString());
        }
        finally
        {
            _playlistWindow?.Close();
            report.Passed = !report.HasErrors;
            report.FinishedAt = DateTimeOffset.Now;
            var fullPath = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(
                fullPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }

        return report.Passed ? 0 : 1;

        void Require(bool condition, string message)
        {
            if (!condition)
            {
                report.Errors.Add(message);
            }
        }

    }

    private void PlayPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_backend.IsPlaying)
        {
            _backend.Pause();
        }
        else
        {
            _backend.Play();
        }
    }

    private void SeekSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_updatingSeek && SeekSlider.IsMouseCaptureWithin)
        {
            _backend.Seek(e.NewValue);
        }
    }

    private void SeekSlider_OnMouseMove(object sender, MouseEventArgs e)
    {
        var width = Math.Max(1, SeekSlider.ActualWidth);
        ShowSeekPreview(Math.Clamp(e.GetPosition(SeekSlider).X / width, 0, 1));
    }

    private void SeekSlider_OnMouseLeave(object sender, MouseEventArgs e)
    {
        SeekPreviewPopup.IsOpen = false;
    }

    private void PlaylistButton_OnClick(object sender, RoutedEventArgs e) => ShowPlaylistWindow();

    private void FullScreenButton_OnClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void FullScreenMenuItem_OnClick(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void VideoSurface_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && !ControlOverlay.IsMouseOver)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    private void VideoSurface_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isFullScreen)
        {
            return;
        }

        ControlOverlay.Visibility = Visibility.Visible;
        _controlsTimer.Stop();
        _controlsTimer.Start();
    }

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isFullScreen)
        {
            ExitFullScreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            ToggleFullScreen();
            e.Handled = true;
        }
    }

    private void Backend_OnStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(UpdatePlaybackUi, DispatcherPriority.Background);
    }

    private void ControlsTimer_OnTick(object? sender, EventArgs e)
    {
        _controlsTimer.Stop();
        if (_isFullScreen)
        {
            ControlOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowPlaylistWindow()
    {
        if (_playlistWindow is null)
        {
            _playlistWindow = new PlaylistWindow { Owner = this };
            _playlistWindow.Closed += (_, _) => _playlistWindow = null;
            _playlistWindow.Show();
        }

        if (_playlistWindow.WindowState == WindowState.Minimized)
        {
            _playlistWindow.WindowState = WindowState.Normal;
        }

        _playlistWindow.Activate();
    }

    private void ShowSeekPreview(double normalizedPosition)
    {
        var sliderWidth = Math.Max(1, SeekSlider.ActualWidth);
        var desiredLeft = normalizedPosition * sliderWidth - SeekPreviewWidth / 2;
        SeekPreviewPopup.HorizontalOffset = Math.Clamp(
            desiredLeft,
            0,
            Math.Max(0, sliderWidth - SeekPreviewWidth));
        SeekPreviewPopup.VerticalOffset = SeekPreviewVerticalOffset;
        SeekPreviewTimeText.Text = FormatTime(_backend.Duration * normalizedPosition);
        SeekPreviewPopup.IsOpen = true;
    }

    private void ToggleFullScreen()
    {
        if (_isFullScreen)
        {
            ExitFullScreen();
        }
        else
        {
            EnterFullScreen();
        }
    }

    private void EnterFullScreen()
    {
        if (_isFullScreen)
        {
            return;
        }

        _savedWindowStyle = WindowStyle;
        _savedWindowState = WindowState;
        _savedResizeMode = ResizeMode;
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        _isFullScreen = true;
        ControlOverlay.Visibility = Visibility.Visible;
        _controlsTimer.Start();
    }

    private void ExitFullScreen()
    {
        if (!_isFullScreen)
        {
            return;
        }

        _controlsTimer.Stop();
        ControlOverlay.Visibility = Visibility.Visible;
        WindowState = WindowState.Normal;
        WindowStyle = _savedWindowStyle;
        ResizeMode = _savedResizeMode;
        WindowState = _savedWindowState;
        _isFullScreen = false;
    }

    private void UpdatePlaybackUi()
    {
        PlayPauseButton.Content = _backend.IsPlaying ? "一時停止" : "再生";
        _updatingSeek = true;
        SeekSlider.Value = _backend.Duration > TimeSpan.Zero
            ? Math.Clamp(_backend.Position.TotalMilliseconds / _backend.Duration.TotalMilliseconds, 0, 1)
            : 0;
        _updatingSeek = false;
        TimeText.Text = $"{FormatTime(_backend.Position)} / {FormatTime(_backend.Duration)}";
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        _controlsTimer.Stop();
        _controlsTimer.Tick -= ControlsTimer_OnTick;
        _backend.StateChanged -= Backend_OnStateChanged;
        _playlistWindow?.Close();
        _backend.Dispose();
    }

    private static string FormatTime(TimeSpan value) => $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";

    private static async Task WaitForLayoutAsync()
    {
        await Task.Delay(150);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
}

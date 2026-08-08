using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BakuretsuOsakanaKobo.Spikes.LibVlcWpf.Playback;
using Microsoft.Win32;

namespace BakuretsuOsakanaKobo.Spikes.LibVlcWpf;

public partial class MainWindow : Window
{
    private readonly LibVlcPlaybackBackend _backend;
    private readonly DispatcherTimer _uiTimer;
    private bool _isSeeking;
    private bool _isClosing;
    private int _requestedVolume = 100;
    private bool _isPerformanceValidation;

    public MainWindow()
    {
        InitializeComponent();

        _backend = new LibVlcPlaybackBackend();
        _backend.ErrorOccurred += Backend_OnErrorOccurred;
        VideoView.MediaPlayer = _backend.MediaPlayer;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _uiTimer.Tick += UiTimer_OnTick;
        _uiTimer.Start();

        Closed += MainWindow_OnClosed;
    }

    private void OpenMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "動画を開く",
            Filter = "対象動画 (*.mp4;*.wmv)|*.mp4;*.wmv|すべてのファイル (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        OpenPath(dialog.FileName);
    }

    internal void OpenPath(string path)
    {
        OverlayStatusText.Text = "読み込み中...";
        if (_backend.Open(path))
        {
            OverlayStatusText.Text = Path.GetFileName(path);
        }
    }

    internal async Task<int> RunPerformanceValidationAsync(
        PerformanceValidationOptions options,
        double processStartToLoadedMs,
        double processStartToDispatcherReadyMs)
    {
        _isPerformanceValidation = true;
        var report = new PerformanceValidationReport
        {
            VideoPath = Path.GetFullPath(options.VideoPath),
            ProcessStartToWindowLoadedMs = processStartToLoadedMs,
            ProcessStartToDispatcherReadyMs = processStartToDispatcherReadyMs,
        };

        var stopwatch = Stopwatch.StartNew();
        var playing = NewCompletionSource();
        var firstClock = NewCompletionSource();
        var firstVout = NewCompletionSource();
        var audioOutput = NewCompletionSource();
        TaskCompletionSource<double>? pendingSeek = null;
        long pendingSeekThresholdMs = long.MaxValue;

        EventHandler<EventArgs> playingHandler = (_, _) => playing.TrySetResult(stopwatch.Elapsed.TotalMilliseconds);
        EventHandler<LibVLCSharp.Shared.MediaPlayerTimeChangedEventArgs> timeHandler = (_, eventArgs) =>
        {
            var elapsed = stopwatch.Elapsed.TotalMilliseconds;
            if (eventArgs.Time >= 100)
            {
                firstClock.TrySetResult(elapsed);
            }

            if (eventArgs.Time >= Volatile.Read(ref pendingSeekThresholdMs))
            {
                Volatile.Read(ref pendingSeek)?.TrySetResult(elapsed);
            }
        };
        EventHandler<LibVLCSharp.Shared.MediaPlayerVoutEventArgs> voutHandler =
            (_, _) => firstVout.TrySetResult(stopwatch.Elapsed.TotalMilliseconds);
        EventHandler<string> logHandler = (_, message) =>
        {
            if (message.Contains("using aout stream module \"wasapi\"", StringComparison.OrdinalIgnoreCase))
            {
                audioOutput.TrySetResult(stopwatch.Elapsed.TotalMilliseconds);
            }

            if (message.Contains("using hw decoder module \"d3d11va\"", StringComparison.OrdinalIgnoreCase))
            {
                report.HardwareDecoder = "d3d11va";
            }
        };

        _backend.MediaPlayer.Playing += playingHandler;
        _backend.MediaPlayer.TimeChanged += timeHandler;
        _backend.MediaPlayer.Vout += voutHandler;
        _backend.LogObserved += logHandler;

        try
        {
            report.IdleSamplingDelayMs = options.IdleSamplingDelay.TotalMilliseconds;
            report.PlaybackSamplingDelayMs = options.SamplingDelay.TotalMilliseconds;
            await Task.Delay(options.IdleSamplingDelay);
            report.OpenRequestedProcessElapsedMs =
                (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            stopwatch.Restart();
            if (!_backend.Open(options.VideoPath))
            {
                throw new InvalidOperationException("LibVLCが再生開始要求を受け付けませんでした。");
            }

            var readiness = await WaitWithTimeoutAsync(
                Task.WhenAll(playing.Task, firstClock.Task, firstVout.Task, audioOutput.Task),
                options.Timeout);
            report.OpenToPlayingMs = readiness[0];
            report.OpenToFirstPlaybackClockMs = readiness[1];
            report.OpenToVideoOutputMs = readiness[2];
            report.OpenToAudioOutputMs = readiness[3];
            report.PlaybackReadyProcessElapsedMs = report.OpenRequestedProcessElapsedMs +
                Math.Max(report.OpenToVideoOutputMs, report.OpenToFirstPlaybackClockMs);

            await Task.Delay(options.SamplingDelay);
            report.Seek50PercentMs = await MeasureSeekAsync(0.50);
            report.Seek90PercentMs = await MeasureSeekAsync(0.90);

            var operation = Stopwatch.StartNew();
            report.RateAccepted = _backend.SetRate(1.5f);
            await WaitUntilAsync(() => Math.Abs(_backend.Rate - 1.5f) < 0.01f, options.Timeout);
            report.RateChangeReflectMs = operation.Elapsed.TotalMilliseconds;
            report.ObservedRate = _backend.Rate;

            operation.Restart();
            _backend.SetVolume(50);
            await WaitUntilAsync(() => _backend.VolumePercent == 50, options.Timeout);
            report.VolumeChangeReflectMs = operation.Elapsed.TotalMilliseconds;
            report.ObservedVolumePercent = _backend.VolumePercent;

            report.WorkingSetBytes = Process.GetCurrentProcess().WorkingSet64;
            report.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
            report.Success = string.Equals(report.HardwareDecoder, "d3d11va", StringComparison.OrdinalIgnoreCase) &&
                report.RateAccepted && Math.Abs(report.ObservedRate - 1.5f) < 0.01f &&
                report.ObservedVolumePercent == 50;
        }
        catch (Exception exception)
        {
            report.Error = exception.ToString();
        }
        finally
        {
            _backend.MediaPlayer.Playing -= playingHandler;
            _backend.MediaPlayer.TimeChanged -= timeHandler;
            _backend.MediaPlayer.Vout -= voutHandler;
            _backend.LogObserved -= logHandler;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ReportPath))!);
            await File.WriteAllTextAsync(
                options.ReportPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }

        return report.Success ? 0 : 1;

        async Task<double> MeasureSeekAsync(double normalizedPosition)
        {
            var length = _backend.LengthMilliseconds;
            if (length <= 0)
            {
                throw new InvalidOperationException("動画の長さを取得できませんでした。");
            }

            var targetMs = (long)(length * normalizedPosition);
            var completion = NewCompletionSource();
            Volatile.Write(ref pendingSeekThresholdMs, targetMs + 100);
            Volatile.Write(ref pendingSeek, completion);
            var requestedAt = stopwatch.Elapsed.TotalMilliseconds;
            _backend.Seek(normalizedPosition);
            var reachedAt = await WaitWithTimeoutAsync(completion.Task, options.Timeout);
            Volatile.Write(ref pendingSeek, null);
            Volatile.Write(ref pendingSeekThresholdMs, long.MaxValue);
            return reachedAt - requestedAt;
        }
    }

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
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

    private void RestartButton_OnClick(object sender, RoutedEventArgs e)
    {
        var currentPath = _backend.CurrentPath;
        if (currentPath is null)
        {
            return;
        }

        var selectedRate = GetSelectedRate();
        if (_backend.Open(currentPath) && selectedRate is not null)
        {
            _backend.SetRate(selectedRate.Value);
        }
    }

    private void SeekSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isSeeking = true;
    }

    private void SeekSlider_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _backend.Seek(SeekSlider.Value);
        _isSeeking = false;
    }

    private void RateComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_backend is null || GetSelectedRate() is not { } rate)
        {
            return;
        }

        if (!_backend.SetRate(rate))
        {
            OverlayStatusText.Text = $"{rate:0.##}倍速への変更に失敗しました";
        }
    }

    private void VolumeSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_backend is null || VolumeText is null)
        {
            return;
        }

        var requested = (int)Math.Round(e.NewValue);
        _requestedVolume = requested;
        _backend.SetVolume(requested);
        UpdateVolumeText();
    }

    private void UiTimer_OnTick(object? sender, EventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        if (!_isSeeking)
        {
            var length = _backend.LengthMilliseconds;
            SeekSlider.Value = length > 0
                ? Math.Clamp((double)_backend.TimeMilliseconds / length, 0d, 1d)
                : 0d;
        }

        TimeText.Text = $"{FormatTime(_backend.TimeMilliseconds)} / {FormatTime(_backend.LengthMilliseconds, roundUp: true)}";
        PlayPauseButton.Content = _backend.IsPlaying ? "一時停止" : "再生";
        UpdateVolumeText();
    }

    private void Backend_OnErrorOccurred(object? sender, string message)
    {
        if (_isPerformanceValidation)
        {
            return;
        }

        Dispatcher.InvokeAsync(() =>
        {
            OverlayStatusText.Text = "再生エラー";
            MessageBox.Show(this, message, "再生エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        _isClosing = true;
        _uiTimer.Stop();
        _uiTimer.Tick -= UiTimer_OnTick;
        VideoView.MediaPlayer = null;
        _backend.ErrorOccurred -= Backend_OnErrorOccurred;
        _backend.Dispose();
    }

    private float? GetSelectedRate()
    {
        if (RateComboBox.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string rateText ||
            !float.TryParse(rateText, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
        {
            return null;
        }

        return rate;
    }

    private static string FormatTime(long milliseconds, bool roundUp = false)
    {
        var normalizedMilliseconds = Math.Max(0, milliseconds);
        if (roundUp && normalizedMilliseconds > 0)
        {
            normalizedMilliseconds = (long)Math.Ceiling(normalizedMilliseconds / 1000d) * 1000;
        }

        var duration = TimeSpan.FromMilliseconds(normalizedMilliseconds);
        var totalHours = (long)duration.TotalHours;
        return $"{totalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private void UpdateVolumeText()
    {
        VolumeText.Text = $"{_requestedVolume}%（実値: {_backend.VolumePercent}%）";
    }

    private static TaskCompletionSource<double> NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<T> WaitWithTimeoutAsync<T>(Task<T> task, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        return await task.WaitAsync(cancellation.Token);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException("状態変更の反映待ちがタイムアウトしました。");
            }

            await Task.Delay(10);
        }
    }
}

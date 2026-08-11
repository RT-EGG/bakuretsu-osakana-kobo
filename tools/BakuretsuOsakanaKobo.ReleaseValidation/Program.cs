using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BakuretsuOsakanaKobo.Infrastructure.Diagnostics;
using BakuretsuOsakanaKobo.Infrastructure.Errors;
using BakuretsuOsakanaKobo.Infrastructure.Persistence;
using BakuretsuOsakanaKobo.Playback;
using LibVLCSharp.Shared;
using NAudio.CoreAudioApi;

namespace BakuretsuOsakanaKobo.ReleaseValidation;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: BakuretsuOsakanaKobo.ReleaseValidation <video> <report.json>");
            return 2;
        }

        var processClock = Stopwatch.StartNew();
        var application = new Application();
        AddProductResources(application.Resources);
        var window = new MainWindow
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        var backend = new LibVlcPlaybackBackend();
        backend.SetMuted(true);
        window.ConfigureServices(
            new PortableDataPaths(AppContext.BaseDirectory),
            new ErrorReporter(new NullDiagnosticLog(), new NullNotificationSink()),
            backend);

        var exitCode = 1;
        window.Loaded += async (_, _) =>
        {
            var windowLoadedMilliseconds = processClock.Elapsed.TotalMilliseconds;
            try
            {
                var seekSlider = (Slider)window.FindName("SeekSlider");
                var volumeSlider = (Slider)window.FindName("VolumeSlider");
                var muteButton = (Button)window.FindName("MuteButton");
                var volumeText = (TextBlock)window.FindName("VolumeText");
                var videoSurface = (UIElement)window.FindName("VideoSurface");
                Ensure(!volumeSlider.IsEnabled && !muteButton.IsEnabled, "Volume controls must start disabled.");
                var openMetrics = await MeasureOpenAsync(window, seekSlider, backend, args[0]);
                var lengthMilliseconds = backend.LengthMilliseconds;
                if (Environment.GetEnvironmentVariable("BOK_AUDIO_DRAIN_VALIDATION") == "1")
                {
                    var drainDiagnostics = await ValidateNaturalDrainAsync(backend);
                    var drainReport = new
                    {
                        success = true,
                        video = Path.GetFullPath(args[0]),
                        muted = backend.IsMuted,
                        processStartToWindowLoadedMs = windowLoadedMilliseconds,
                        openToTimelineReadyMs = openMetrics.TimelineReadyMilliseconds,
                        openToAudioOutputReadyMs = openMetrics.AudioOutputReadyMilliseconds,
                        lengthMilliseconds,
                        audioDiagnostics = drainDiagnostics,
                    };
                    WriteReport(args[1], drainReport);
                    Console.WriteLine(JsonSerializer.Serialize(drainReport));
                    exitCode = 0;
                    return;
                }

                var seek50Milliseconds = await MeasureSeekAsync(
                    seekSlider,
                    backend,
                    normalizedPosition: 0.5,
                    lengthMilliseconds);
                var seek90Milliseconds = await MeasureSeekAsync(
                    seekSlider,
                    backend,
                    normalizedPosition: 0.9,
                    lengthMilliseconds);

                var volumeValidation = ValidateVolumeControls(
                    volumeSlider,
                    muteButton,
                    volumeText,
                    videoSurface,
                    backend);
                var audibleValidation = Environment.GetEnvironmentVariable("BOK_AUDIBLE_VOLUME_VALIDATION") == "1"
                    ? await ValidateAudibleVolumeAsync(backend)
                    : null;

                var report = new
                {
                    success = true,
                    video = Path.GetFullPath(args[0]),
                    muted = backend.IsMuted,
                    processStartToWindowLoadedMs = windowLoadedMilliseconds,
                    openToTimelineReadyMs = openMetrics.TimelineReadyMilliseconds,
                    openToAudioOutputReadyMs = openMetrics.AudioOutputReadyMilliseconds,
                    seek50PercentMs = seek50Milliseconds,
                    seek90PercentMs = seek90Milliseconds,
                    lengthMilliseconds,
                    volumeValidation,
                    audibleValidation,
                    audioDiagnostics = backend.AudioDiagnostics,
                };
                WriteReport(args[1], report);
                Console.WriteLine(JsonSerializer.Serialize(report));
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
            }
            finally
            {
                window.Close();
            }
        };

        application.Run(window);
        var shutdownDiagnostics = backend.AudioDiagnostics;
        if (shutdownDiagnostics.RenderThreadAlive || shutdownDiagnostics.Failed)
        {
            Console.Error.WriteLine(
                $"Audio shutdown failed: threadAlive={shutdownDiagnostics.RenderThreadAlive}, failed={shutdownDiagnostics.Failed}.");
            return 1;
        }

        return exitCode;
    }

    private static async Task<RealtimeAudioDiagnostics> ValidateNaturalDrainAsync(
        LibVlcPlaybackBackend backend)
    {
        await WaitUntilAsync(
            () => backend.AudioDiagnostics is { CompletedDrainCount: > 0, PendingLimiterFrames: 0, Failed: false },
            TimeSpan.FromSeconds(10),
            "The product PCM/WASAPI path did not drain after natural playback completion.");
        var diagnostics = backend.AudioDiagnostics;
        Ensure(diagnostics.UnderrunCount == 0, "Natural drain reported a PCM underrun.");
        Ensure(diagnostics.OverflowCount == 0, "Natural drain reported a PCM overflow.");
        Ensure(diagnostics.NonFiniteInputSamples == 0, "Natural drain received non-finite PCM input.");
        Ensure(diagnostics.NonFiniteOutputSamples == 0, "Natural drain produced non-finite PCM output.");
        Ensure(diagnostics.OverRangeSamples == 0, "Natural drain produced an out-of-range PCM sample.");
        Ensure(
            diagnostics.InputFrames == diagnostics.BufferedFrames + diagnostics.DiscardedLimiterFrames,
            "Natural drain did not account for every input frame.");
        var flushedFrames = diagnostics.FlushedBytes /
            (sizeof(float) * RealtimeVolumeProcessor.Channels);
        Ensure(
            diagnostics.BufferedFrames == diagnostics.ConsumedFrames + flushedFrames,
            $"Natural drain did not account for every buffered frame: buffered={diagnostics.BufferedFrames}, consumed={diagnostics.ConsumedFrames}, flushed={flushedFrames}.");
        return diagnostics;
    }

    private static async Task<object> ValidateAudibleVolumeAsync(LibVlcPlaybackBackend backend)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var endpointVolume = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
        var endpointMuted = endpoint.AudioEndpointVolume.Mute;
        Ensure(!endpointMuted, "Audible validation requires the default endpoint to be unmuted.");
        Ensure(
            endpointVolume <= 0.5 + 0.000001,
            $"Audible validation requires endpoint volume at 50% or lower; current value is {endpointVolume:P1}.");

        backend.SetVolumePercent(PlaybackVolume.MaximumPercent);
        try
        {
            backend.SetMuted(false);
            backend.Play();
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        finally
        {
            backend.SetMuted(true);
            backend.Pause();
        }

        var diagnostics = backend.AudioDiagnostics;
        var ceiling = Math.Pow(10, RealtimeVolumeProcessor.BoostedCeilingDecibels / 20);
        Ensure(!diagnostics.Failed, "The audible PCM/WASAPI path reported a failure.");
        Ensure(diagnostics.OverRangeSamples == 0, "The audible PCM/WASAPI path exceeded full scale.");
        Ensure(diagnostics.Peak <= ceiling + 0.000001, "The audible PCM/WASAPI path exceeded the -1 dBFS ceiling.");

        return new
        {
            durationSeconds = 3,
            volumePercent = PlaybackVolume.MaximumPercent,
            endpointVolumePercent = endpointVolume * 100,
            peak = diagnostics.Peak,
            ceiling,
            diagnostics.OverRangeSamples,
            finalMuted = backend.IsMuted,
        };
    }

    private static object ValidateVolumeControls(
        Slider volumeSlider,
        Button muteButton,
        TextBlock volumeText,
        UIElement videoSurface,
        LibVlcPlaybackBackend backend)
    {
        Ensure(volumeSlider.IsEnabled && muteButton.IsEnabled, "Volume controls did not become enabled.");
        Ensure(backend.IsMuted, "Validation playback must remain muted.");

        volumeSlider.Value = 65;
        Ensure(backend.VolumePercent == 65, "The slider did not update backend volume to 65%.");
        Ensure(volumeText.Text == "65%", "The volume label did not update to 65%.");

        backend.Pause();
        muteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ensure(!backend.IsMuted, "The mute button did not unmute paused playback.");
        muteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ensure(backend.IsMuted, "The mute button did not restore mute.");

        volumeSlider.Value = 495;
        var wheelUp = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
        };
        videoSurface.RaiseEvent(wheelUp);
        Ensure(wheelUp.Handled && backend.VolumePercent == 500, "Wheel-up did not reach 500%.");

        var wheelAboveMaximum = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
        };
        videoSurface.RaiseEvent(wheelAboveMaximum);
        Ensure(backend.VolumePercent == 500, "Wheel-up exceeded the 500% product limit.");

        var wheelDown = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
        };
        videoSurface.RaiseEvent(wheelDown);
        Ensure(wheelDown.Handled && backend.VolumePercent == 495, "Wheel-down did not reduce volume by 5%.");

        return new
        {
            sliderPercent = 65,
            muteToggle = true,
            wheelStepPercent = PlaybackVolume.WheelStepPercent,
            maximumPercent = PlaybackVolume.MaximumPercent,
            finalPercent = backend.VolumePercent,
            finalMuted = backend.IsMuted,
        };
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task<OpenMetrics> MeasureOpenAsync(
        MainWindow window,
        Slider seekSlider,
        LibVlcPlaybackBackend backend,
        string path)
    {
        var clock = Stopwatch.StartNew();
        var videoOutput = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var playbackClock = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnVideoOutput(object? sender, MediaPlayerVoutEventArgs eventArgs) =>
            videoOutput.TrySetResult(clock.Elapsed.TotalMilliseconds);

        void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs eventArgs)
        {
            if (eventArgs.Time >= 100)
            {
                playbackClock.TrySetResult(clock.Elapsed.TotalMilliseconds);
            }
        }

        backend.MediaPlayer.Vout += OnVideoOutput;
        backend.MediaPlayer.TimeChanged += OnTimeChanged;
        try
        {
            await window.OpenVideoAsync(path);
            var ready = await Task.WhenAll(videoOutput.Task, playbackClock.Task).WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => seekSlider.IsEnabled && backend.LengthMilliseconds > 0,
                TimeSpan.FromSeconds(5),
                "The product timeline did not become ready.");
            var timelineReadyMilliseconds = ready.Max();
            await WaitUntilAsync(
                () => backend.AudioDiagnostics is { CallbackCount: > 0, OutputStarted: true, Failed: false },
                TimeSpan.FromSeconds(5),
                "The product PCM/WASAPI path did not become ready.");
            return new OpenMetrics(timelineReadyMilliseconds, clock.Elapsed.TotalMilliseconds);
        }
        finally
        {
            backend.MediaPlayer.Vout -= OnVideoOutput;
            backend.MediaPlayer.TimeChanged -= OnTimeChanged;
        }
    }

    private static async Task<double> MeasureSeekAsync(
        Slider seekSlider,
        IPlaybackBackend backend,
        double normalizedPosition,
        long lengthMilliseconds)
    {
        var targetMilliseconds = (long)(lengthMilliseconds * normalizedPosition);
        var clock = Stopwatch.StartNew();
        seekSlider.Value = normalizedPosition;
        await WaitUntilAsync(
            () => backend.TimeMilliseconds >= Math.Min(lengthMilliseconds, targetMilliseconds + 100),
            TimeSpan.FromSeconds(3),
            $"The {normalizedPosition:P0} seek did not reach its target.");
        return clock.Elapsed.TotalMilliseconds;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failureMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        if (!condition())
        {
            throw new TimeoutException(failureMessage);
        }
    }

    private static void WriteReport(string path, object report)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void AddProductResources(ResourceDictionary resources)
    {
        resources["AppBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(0x0C, 0x11, 0x19));
        resources["TextBrush"] = new SolidColorBrush(Color.FromRgb(0xF2, 0xF6, 0xFC));
        resources["MutedTextBrush"] = new SolidColorBrush(Color.FromRgb(0x9D, 0xAB, 0xC0));
        resources["PanelBrush"] = new SolidColorBrush(Color.FromRgb(0x15, 0x1D, 0x29));
        resources["BorderBrush"] = new SolidColorBrush(Color.FromRgb(0x34, 0x41, 0x56));
    }

    private sealed class NullDiagnosticLog : IDiagnosticLog
    {
        public DiagnosticWriteResult Write(DiagnosticEvent diagnosticEvent) => new(true, null);

        public void Dispose()
        {
        }
    }

    private readonly record struct OpenMetrics(
        double TimelineReadyMilliseconds,
        double AudioOutputReadyMilliseconds);

    private sealed class NullNotificationSink : IUserNotificationSink
    {
        public void Show(UserNotification notification)
        {
        }
    }
}

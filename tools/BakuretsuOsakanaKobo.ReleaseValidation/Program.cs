using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BakuretsuOsakanaKobo.Infrastructure.Diagnostics;
using BakuretsuOsakanaKobo.Infrastructure.Errors;
using BakuretsuOsakanaKobo.Infrastructure.Persistence;
using BakuretsuOsakanaKobo.Playback;
using LibVLCSharp.Shared;

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
        backend.MediaPlayer.Mute = true;
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
                var openMilliseconds = await MeasureOpenAsync(window, seekSlider, backend, args[0]);
                var lengthMilliseconds = backend.LengthMilliseconds;

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

                var report = new
                {
                    success = true,
                    video = Path.GetFullPath(args[0]),
                    muted = backend.MediaPlayer.Mute,
                    processStartToWindowLoadedMs = windowLoadedMilliseconds,
                    openToTimelineReadyMs = openMilliseconds,
                    seek50PercentMs = seek50Milliseconds,
                    seek90PercentMs = seek90Milliseconds,
                    lengthMilliseconds,
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
        return exitCode;
    }

    private static async Task<double> MeasureOpenAsync(
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
            return ready.Max();
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

    private sealed class NullNotificationSink : IUserNotificationSink
    {
        public void Show(UserNotification notification)
        {
        }
    }
}

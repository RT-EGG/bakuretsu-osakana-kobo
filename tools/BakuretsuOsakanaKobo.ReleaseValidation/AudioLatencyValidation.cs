using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using BakuretsuOsakanaKobo.Playback;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BakuretsuOsakanaKobo.ReleaseValidation;

internal static class AudioLatencyValidation
{
    private const double ActiveRmsThreshold = 0.001;
    private const double SilentRmsThreshold = 0.0001;
    private const int AnalysisWindowMilliseconds = 10;
    private const int ButtonConfirmationWindows = 10;
    private const int VolumeConfirmationWindows = 40;

    public static async Task<object> RunAsync(
        Slider volumeSlider,
        Button muteButton,
        LibVlcPlaybackBackend backend)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var endpointVolume = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
        Ensure(!endpoint.AudioEndpointVolume.Mute,
            "Audio-latency validation requires the default endpoint to be unmuted.");
        Ensure(endpointVolume <= 0.5 + 0.000001,
            "Audio-latency validation requires the default endpoint volume to be 50% or lower; " +
            $"current value is {endpointVolume:P1}.");

        backend.SetVolumePercent(100);
        backend.SetMuted(false);
        backend.Play();
        await WaitUntilAsync(
            () => backend.AudioDiagnostics is
            {
                OutputStarted: true,
                BufferedBytes: > 0,
                ConsumedFrames: > 0,
                Peak: > ActiveRmsThreshold,
                Failed: false,
            },
            TimeSpan.FromSeconds(5),
            "The PCM/WASAPI path did not begin producing audible audio for audio-latency validation.");

        using var capture = new TimedLoopbackCapture(endpoint);
        try
        {
            capture.Start();
            await capture.WaitForAsync(
                afterMilliseconds: 0,
                block => block.Rms >= ActiveRmsThreshold,
                requiredConsecutiveMatches: ButtonConfirmationWindows,
                TimeSpan.FromSeconds(2));
            var warmupMuteTimestamp = capture.ElapsedMilliseconds;
            backend.SetMuted(true);
            await capture.WaitForAsync(
                warmupMuteTimestamp,
                block => block.Rms <= SilentRmsThreshold,
                requiredConsecutiveMatches: ButtonConfirmationWindows,
                TimeSpan.FromSeconds(2));

            TransitionMeasurement unmute;
            try
            {
                unmute = await MeasureButtonTransitionAsync(
                    "unmute-100",
                    muteButton,
                    backend,
                    expectedMuted: false,
                    capture,
                    block => block.Rms >= ActiveRmsThreshold);
            }
            catch (TimeoutException exception)
            {
                var timeoutDiagnostics = backend.AudioDiagnostics;
                throw new TimeoutException(
                    "The first audible transition timed out after PCM consumption began. " +
                    $"inputFrames={timeoutDiagnostics.InputFrames}, " +
                    $"bufferedFrames={timeoutDiagnostics.BufferedFrames}, " +
                    $"consumedFrames={timeoutDiagnostics.ConsumedFrames}, " +
                    $"rawBufferedBytes={timeoutDiagnostics.BufferedBytes}, " +
                    $"peak={timeoutDiagnostics.Peak:0.000000}, " +
                    $"underruns={timeoutDiagnostics.UnderrunCount}, " +
                    $"failed={timeoutDiagnostics.Failed}.",
                    exception);
            }
            await Task.Delay(250);
            var mute = await MeasureButtonTransitionAsync(
                "mute-100",
                muteButton,
                backend,
                expectedMuted: true,
                capture,
                block => block.Rms <= SilentRmsThreshold);

            volumeSlider.Value = 25;
            var boostedUnmute = await MeasureButtonTransitionAsync(
                "unmute-25",
                muteButton,
                backend,
                expectedMuted: false,
                capture,
                block => block.Rms >= ActiveRmsThreshold / 4);
            await Task.Delay(250);
            var baselineRms = capture.MedianRecentRms(TimeSpan.FromMilliseconds(200));
            Ensure(baselineRms > 0, "Could not establish the 25% loopback baseline.");

            var boost = await MeasureSliderTransitionAsync(
                "volume-25-to-500",
                volumeSlider,
                500,
                backend,
                capture,
                block => block.Rms >= baselineRms * 1.8,
                requiredConsecutiveMatches: VolumeConfirmationWindows);
            await Task.Delay(250);
            var boostedRms = capture.MedianRecentRms(TimeSpan.FromMilliseconds(200));
            Ensure(boostedRms > baselineRms,
                $"The 500% loopback level did not exceed the 25% baseline: {baselineRms} -> {boostedRms}.");

            var reduce = await MeasureSliderTransitionAsync(
                "volume-500-to-25",
                volumeSlider,
                25,
                backend,
                capture,
                block => block.Rms <= boostedRms * 0.6,
                requiredConsecutiveMatches: VolumeConfirmationWindows);
            var finalMute = await MeasureButtonTransitionAsync(
                "mute-25",
                muteButton,
                backend,
                expectedMuted: true,
                capture,
                block => block.Rms <= SilentRmsThreshold);

            var diagnostics = backend.AudioDiagnostics;
            Ensure(!diagnostics.Failed, "The audio path reported a failure during latency validation.");
            Ensure(diagnostics.UnderrunCount == 0, "Audio latency validation reported a PCM underrun.");
            Ensure(diagnostics.OverflowCount == 0, "Audio latency validation overflowed the PCM buffer.");
            Ensure(diagnostics.NonFiniteInputSamples == 0 && diagnostics.NonFiniteOutputSamples == 0,
                "Audio latency validation observed non-finite samples.");
            Ensure(diagnostics.OverRangeSamples == 0,
                "Audio latency validation produced samples outside full scale.");

            return new
            {
                endpoint = endpoint.FriendlyName,
                endpointVolumePercent = endpointVolume * 100,
                captureFormat = capture.WaveFormat.ToString(),
                unmute,
                mute,
                boostedUnmute,
                baselineRms,
                boost,
                boostedRms,
                reduce,
                finalMute,
                diagnostics,
            };
        }
        finally
        {
            backend.SetMuted(true);
            backend.Pause();
            await capture.StopAsync();
        }
    }

    private static async Task<TransitionMeasurement> MeasureButtonTransitionAsync(
        string operation,
        Button muteButton,
        LibVlcPlaybackBackend backend,
        bool expectedMuted,
        TimedLoopbackCapture capture,
        Func<AudioLevelBlock, bool> outputPredicate)
    {
        var before = backend.AudioDiagnostics;
        var operationTimestamp = capture.ElapsedMilliseconds;
        muteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, muteButton));
        var backendTimestamp = capture.ElapsedMilliseconds;
        Ensure(backend.IsMuted == expectedMuted, $"{operation} did not update the backend mute value.");
        var after = backend.AudioDiagnostics;
        var output = await capture.WaitForAsync(
            operationTimestamp,
            outputPredicate,
            requiredConsecutiveMatches: ButtonConfirmationWindows,
            TimeSpan.FromSeconds(4));
        return CreateMeasurement(operation, operationTimestamp, backendTimestamp, before, after, output);
    }

    private static async Task<TransitionMeasurement> MeasureSliderTransitionAsync(
        string operation,
        Slider volumeSlider,
        int expectedPercent,
        LibVlcPlaybackBackend backend,
        TimedLoopbackCapture capture,
        Func<AudioLevelBlock, bool> outputPredicate,
        int requiredConsecutiveMatches = 2)
    {
        var before = backend.AudioDiagnostics;
        var operationTimestamp = capture.ElapsedMilliseconds;
        volumeSlider.Value = expectedPercent;
        var backendTimestamp = capture.ElapsedMilliseconds;
        Ensure(backend.VolumePercent == expectedPercent,
            $"{operation} did not update backend volume to {expectedPercent}%.");
        var after = backend.AudioDiagnostics;
        var output = await capture.WaitForAsync(
            operationTimestamp,
            outputPredicate,
            requiredConsecutiveMatches,
            TimeSpan.FromSeconds(4));
        return CreateMeasurement(operation, operationTimestamp, backendTimestamp, before, after, output);
    }

    private static TransitionMeasurement CreateMeasurement(
        string operation,
        double operationTimestamp,
        double backendTimestamp,
        RealtimeAudioDiagnostics before,
        RealtimeAudioDiagnostics after,
        AudioLevelBlock output) =>
        new(
            operation,
            backendTimestamp - operationTimestamp,
            output.TimelineMilliseconds - operationTimestamp,
            after.LastLevelChangeBufferedBytes,
            after.LastLevelChangeBufferedMilliseconds,
            before.LevelChangeCount,
            after.LevelChangeCount,
            output.Peak,
            output.Rms);

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string failureMessage)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed >= timeout)
            {
                throw new TimeoutException(failureMessage);
            }

            await Task.Delay(10);
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class TimedLoopbackCapture : IDisposable
    {
        private readonly object _sync = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly WasapiLoopbackCapture _capture;
        private readonly List<AudioLevelBlock> _blocks = [];
        private readonly TaskCompletionSource _stopped =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _started;
        private bool _disposed;

        public TimedLoopbackCapture(MMDevice endpoint)
        {
            _capture = new WasapiLoopbackCapture(endpoint);
            WaveFormat = _capture.WaveFormat;
            Ensure(WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat && WaveFormat.BitsPerSample == 32,
                $"Unsupported loopback format: {WaveFormat}.");
            _capture.DataAvailable += CaptureOnDataAvailable;
            _capture.RecordingStopped += CaptureOnRecordingStopped;
        }

        public WaveFormat WaveFormat { get; }

        public double ElapsedMilliseconds => _clock.Elapsed.TotalMilliseconds;

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _capture.StartRecording();
            _started = true;
        }

        public async Task<AudioLevelBlock> WaitForAsync(
            double afterMilliseconds,
            Func<AudioLevelBlock, bool> predicate,
            int requiredConsecutiveMatches,
            TimeSpan timeout)
        {
            Ensure(requiredConsecutiveMatches > 0,
                "The required consecutive loopback block count must be positive.");
            var clock = Stopwatch.StartNew();
            var inspected = 0;
            var consecutiveMatches = 0;
            var firstMatch = default(AudioLevelBlock);
            while (clock.Elapsed < timeout)
            {
                AudioLevelBlock[] available;
                lock (_sync)
                {
                    available = _blocks.Skip(inspected).ToArray();
                    inspected = _blocks.Count;
                }

                foreach (var block in available)
                {
                    if (block.TimelineMilliseconds < afterMilliseconds)
                    {
                        continue;
                    }

                    if (predicate(block))
                    {
                        if (consecutiveMatches == 0)
                        {
                            firstMatch = block;
                        }

                        consecutiveMatches++;
                    }
                    else
                    {
                        consecutiveMatches = 0;
                    }

                    if (consecutiveMatches >= requiredConsecutiveMatches)
                    {
                        return firstMatch;
                    }
                }

                await Task.Delay(5);
            }

            AudioLevelBlock[] observed;
            lock (_sync)
            {
                observed = _blocks
                    .Where(block => block.TimelineMilliseconds >= afterMilliseconds)
                    .ToArray();
            }

            throw new TimeoutException(
                "The expected WASAPI loopback level transition was not observed. " +
                $"blocks={observed.Length}, " +
                $"maxPeak={(observed.Length == 0 ? 0 : observed.Max(block => block.Peak)):0.000000}, " +
                $"maxRms={(observed.Length == 0 ? 0 : observed.Max(block => block.Rms)):0.000000}.");
        }

        public double MedianRecentRms(TimeSpan duration)
        {
            var threshold = ElapsedMilliseconds - duration.TotalMilliseconds;
            double[] values;
            lock (_sync)
            {
                values = _blocks
                    .Where(block => block.TimelineMilliseconds >= threshold)
                    .Select(block => block.Rms)
                    .Order()
                    .ToArray();
            }

            return values.Length == 0 ? 0 : values[values.Length / 2];
        }

        public async Task StopAsync()
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            _capture.StopRecording();
            await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _capture.DataAvailable -= CaptureOnDataAvailable;
            _capture.RecordingStopped -= CaptureOnRecordingStopped;
            _capture.Dispose();
        }

        private void CaptureOnDataAvailable(object? sender, WaveInEventArgs eventArgs)
        {
            try
            {
                var samples = MemoryMarshal.Cast<byte, float>(
                    eventArgs.Buffer.AsSpan(0, eventArgs.BytesRecorded));
                var channels = WaveFormat.Channels;
                var totalFrames = samples.Length / channels;
                var windowFrames = Math.Max(
                    1,
                    WaveFormat.SampleRate * AnalysisWindowMilliseconds / 1000);
                var captureEndMilliseconds = ElapsedMilliseconds;
                var captureStartMilliseconds = captureEndMilliseconds -
                    (totalFrames * 1000.0 / WaveFormat.SampleRate);
                for (var startFrame = 0; startFrame < totalFrames; startFrame += windowFrames)
                {
                    var endFrame = Math.Min(startFrame + windowFrames, totalFrames);
                    var window = samples.Slice(
                        startFrame * channels,
                        (endFrame - startFrame) * channels);
                    double squared = 0;
                    double peak = 0;
                    foreach (var sample in window)
                    {
                        if (!float.IsFinite(sample))
                        {
                            throw new InvalidOperationException("Loopback capture contained a non-finite sample.");
                        }

                        var magnitude = Math.Abs(sample);
                        peak = Math.Max(peak, magnitude);
                        squared += sample * sample;
                    }

                    var rms = window.Length == 0 ? 0 : Math.Sqrt(squared / window.Length);
                    var windowEndMilliseconds = captureStartMilliseconds +
                        (endFrame * 1000.0 / WaveFormat.SampleRate);
                    var block = new AudioLevelBlock(windowEndMilliseconds, peak, rms);
                    lock (_sync)
                    {
                        _blocks.Add(block);
                    }
                }
            }
            catch (Exception exception)
            {
                _stopped.TrySetException(exception);
                _capture.StopRecording();
            }
        }

        private void CaptureOnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
        {
            if (eventArgs.Exception is not null)
            {
                _stopped.TrySetException(eventArgs.Exception);
            }
            else
            {
                _stopped.TrySetResult();
            }
        }

    }

    private readonly record struct AudioLevelBlock(
        double TimelineMilliseconds,
        double Peak,
        double Rms);
}

internal sealed record TransitionMeasurement(
    string Operation,
    double BackendUpdateMilliseconds,
    double LoopbackTransitionMilliseconds,
    int BufferedBytesAtChange,
    double BufferedMillisecondsAtChange,
    long LevelChangeCountBefore,
    long LevelChangeCountAfter,
    double TransitionBlockPeak,
    double TransitionBlockRms);

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using LibVLCSharp.Shared;
using NAudio.CoreAudioApi;
using NAudio.Wave;

const int sampleRate = 48_000;
const int channels = 2;
const int consumerPeriodMilliseconds = 10;
const int prebufferMilliseconds = 100;
const double limiterCeilingDb = -1;

if (args.Length is < 2 or > 8)
{
    Console.Error.WriteLine(
        "Usage: LibVlcRealtimeAudioProbe <media-path> <output-json> [volume-percent] [simulated|wasapi-smoke] [post-limiter-attenuation-db] [max-endpoint-volume-percent] [none|transitions|rate-transitions|avsync] [default|low-latency|no-stretch]");
    return 2;
}

var mediaPath = Path.GetFullPath(args[0]);
var outputPath = Path.GetFullPath(args[1]);
var volumePercent = args.Length == 3 && int.TryParse(args[2], out var parsedVolume)
    ? parsedVolume
    : 500;
if (args.Length >= 3 && int.TryParse(args[2], out var suppliedVolume)) volumePercent = suppliedVolume;
var mode = args.Length >= 4 ? args[3] : "simulated";
var wasapiSmoke = string.Equals(mode, "wasapi-smoke", StringComparison.OrdinalIgnoreCase);
if (!wasapiSmoke && !string.Equals(mode, "simulated", StringComparison.OrdinalIgnoreCase))
    throw new ArgumentOutOfRangeException(nameof(mode), mode, "Expected simulated or wasapi-smoke.");
var safetyAttenuationDb = args.Length >= 5 && double.TryParse(args[4], out var suppliedAttenuation)
    ? suppliedAttenuation
    : -40;
if (wasapiSmoke && (safetyAttenuationDb < -40 || safetyAttenuationDb > 0))
    throw new ArgumentOutOfRangeException(
        nameof(safetyAttenuationDb), safetyAttenuationDb, "Expected -40 through 0 dB.");
if (!wasapiSmoke) safetyAttenuationDb = 0;
var postLimiterGain = Math.Pow(10, safetyAttenuationDb / 20);
var maximumEndpointVolumePercent = args.Length >= 6 && double.TryParse(args[5], out var suppliedEndpointLimit)
    ? suppliedEndpointLimit
    : 5;
if (maximumEndpointVolumePercent is < 1 or > 100)
    throw new ArgumentOutOfRangeException(
        nameof(maximumEndpointVolumePercent), maximumEndpointVolumePercent, "Expected 1 through 100 percent.");
var maximumEndpointVolumeScalar = maximumEndpointVolumePercent / 100;
var operationScenario = args.Length >= 7 ? args[6] : "none";
var runTransitions = string.Equals(operationScenario, "transitions", StringComparison.OrdinalIgnoreCase);
var runRateTransitions = string.Equals(operationScenario, "rate-transitions", StringComparison.OrdinalIgnoreCase);
var showVideo = string.Equals(operationScenario, "avsync", StringComparison.OrdinalIgnoreCase);
if (!runTransitions && !runRateTransitions && !showVideo &&
    !string.Equals(operationScenario, "none", StringComparison.OrdinalIgnoreCase))
    throw new ArgumentOutOfRangeException(nameof(operationScenario), operationScenario, "Expected none, transitions, rate-transitions, or avsync.");
var timeStretchProfile = args.Length == 8 ? args[7] : "default";
if (timeStretchProfile is not ("default" or "low-latency" or "no-stretch"))
    throw new ArgumentOutOfRangeException(nameof(timeStretchProfile), timeStretchProfile, "Expected default, low-latency, or no-stretch.");
if (!File.Exists(mediaPath)) throw new FileNotFoundException("Media file not found.", mediaPath);
if (volumePercent is < 100 or > 500) throw new ArgumentOutOfRangeException(nameof(volumePercent));
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

var report = new ProbeReport
{
    StartedAt = DateTimeOffset.Now,
    MediaPath = mediaPath,
    VolumePercent = volumePercent,
    Mode = mode,
    PostLimiterSafetyAttenuationDb = safetyAttenuationDb,
    MaximumEndpointVolumePercent = maximumEndpointVolumePercent,
    OperationScenario = operationScenario,
    TimeStretchProfile = timeStretchProfile,
    Safety = wasapiSmoke
        ? $"WASAPI smoke test: output is post-limiter attenuated by {-safetyAttenuationDb:0} dB and playback is blocked unless the default endpoint is muted or at {maximumEndpointVolumePercent:0.#}% volume or lower."
        : "No WASAPI output was opened. The default render endpoint was queried read-only, and PCM was consumed by a simulated timer.",
};

try
{
    using var enumerator = new MMDeviceEnumerator();
    using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    report.DefaultEndpoint = new EndpointInfo
    {
        FriendlyName = endpoint.FriendlyName,
        Id = endpoint.ID,
        State = endpoint.State.ToString(),
        MixFormat = endpoint.AudioClient.MixFormat.ToString(),
        MasterVolumeScalar = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar,
        Muted = endpoint.AudioEndpointVolume.Mute,
    };
}
catch (Exception exception)
{
    report.Warnings.Add($"Could not query the default render endpoint: {exception.Message}");
}

var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
var buffer = new BufferedWaveProvider(waveFormat)
{
    BufferDuration = TimeSpan.FromSeconds(2),
    DiscardOnBufferOverflow = false,
    ReadFully = false,
};
if (wasapiSmoke && report.DefaultEndpoint is not null &&
    report.DefaultEndpoint.MasterVolumeScalar > maximumEndpointVolumeScalar + 0.000001 && !report.DefaultEndpoint.Muted)
{
    report.Errors.Add(
        $"Safety guard blocked playback: default endpoint volume is {report.DefaultEndpoint.MasterVolumeScalar:P1}; expected {maximumEndpointVolumePercent:0.#}% or lower (or muted).");
}
var limiter = new StreamingLookaheadLimiter(
    channels,
    sampleRate,
    volumePercent / 100.0,
    DecibelsToLinear(limiterCeilingDb),
    lookaheadMilliseconds: 5,
    releaseMilliseconds: 80);
var sync = new object();
var callbackErrors = new List<string>();
var firstBuffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var playbackEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var playbackFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var safetyStopRequested = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
long callbackCount = 0;
long inputFrames = 0;
long bufferedFrames = 0;
long overflowCount = 0;
long isDraining = 0;
long drainCallbackCount = 0;
long flushCallbackCount = 0;
long flushedBytes = 0;
long discardedLimiterFrames = 0;
long audioPaused = 0;
double renderPeak = 0;
long lastCallbackTimestamp = 0;
double maximumCallbackGapMilliseconds = 0;
long requestedRateMilli = 1_000;
var callbackObservationSync = new object();
var callbackObservations = new List<CallbackObservation>();
var diagnosticLogs = new ConcurrentQueue<string>();
var stopwatch = Stopwatch.StartNew();

Core.Initialize();
var libVlcOptions = new List<string> { "--no-video-title-show", "--file-caching=150" };
if (!showVideo) libVlcOptions.Add("--no-video");
if (timeStretchProfile == "low-latency")
{
    libVlcOptions.Add("--scaletempo-stride=10");
    libVlcOptions.Add("--scaletempo-overlap=0.20");
    libVlcOptions.Add("--scaletempo-search=3");
}
else if (timeStretchProfile == "no-stretch")
{
    libVlcOptions.Add("--no-audio-time-stretch");
}
using var libVlc = new LibVLC(runRateTransitions, libVlcOptions.ToArray());
if (runRateTransitions) libVlc.Log += (_, eventArgs) =>
{
    var text = $"{eventArgs.Module}: {eventArgs.Message}";
    if (diagnosticLogs.Count < 200 && IsRelevantLibVlcLog(text))
        diagnosticLogs.Enqueue(text);
};
using var media = new Media(libVlc, new Uri(mediaPath));
using var player = new MediaPlayer(libVlc);

MediaPlayer.LibVLCAudioPlayCb playCallback = OnAudioPlay;
MediaPlayer.LibVLCAudioPauseCb pauseCallback = (_, _) => Interlocked.Exchange(ref audioPaused, 1);
MediaPlayer.LibVLCAudioResumeCb resumeCallback = (_, _) => Interlocked.Exchange(ref audioPaused, 0);
MediaPlayer.LibVLCAudioFlushCb flushCallback = (_, _) =>
{
    lock (sync)
    {
        Interlocked.Increment(ref flushCallbackCount);
        Interlocked.Add(ref flushedBytes, buffer.BufferedBytes);
        Interlocked.Add(ref discardedLimiterFrames, limiter.PendingFrames);
        buffer.ClearBuffer();
        limiter.Reset();
    }
};
MediaPlayer.LibVLCAudioDrainCb drainCallback = _ =>
{
    try
    {
        Interlocked.Increment(ref drainCallbackCount);
        lock (sync)
        {
            AddToBuffer(limiter.Flush());
        }
        Interlocked.Exchange(ref isDraining, 1);
        var deadline = Stopwatch.StartNew();
        while (buffer.BufferedBytes > 0 && deadline.Elapsed < TimeSpan.FromSeconds(3))
        {
            Thread.Sleep(2);
        }
        if (buffer.BufferedBytes > 0)
        {
            lock (callbackErrors)
            {
                callbackErrors.Add(
                    $"Drain callback timed out with {buffer.BufferedBytes} buffered byte(s).");
            }
        }
    }
    catch (Exception exception)
    {
        lock (callbackErrors) callbackErrors.Add($"Drain callback: {exception.Message}");
    }
};

player.SetAudioCallbacks(
    playCallback,
    pauseCallback,
    resumeCallback,
    flushCallback,
    drainCallback);
player.SetAudioFormat("S16N", sampleRate, channels);
player.EndReached += (_, _) => playbackEnded.TrySetResult();
player.EncounteredError += (_, _) => playbackFailed.TrySetResult();

using var consumerCancellation = new CancellationTokenSource();
var consumer = wasapiSmoke
    ? ConsumeWasapiAsync(buffer, firstBuffered.Task, playbackEnded.Task, consumerCancellation.Token)
    : ConsumeBufferAsync(buffer, firstBuffered.Task, playbackEnded.Task, consumerCancellation.Token);
var operationMetrics = new OperationMetrics { Scenario = operationScenario };
Task operationTask = Task.CompletedTask;
if (report.Errors.Count != 0)
{
    playbackEnded.TrySetResult();
}
else if (!player.Play(media))
{
    report.Errors.Add("MediaPlayer.Play returned false.");
}
else
{
    if (runTransitions)
        operationTask = RunTransitionScenarioAsync(operationMetrics);
    else if (runRateTransitions)
        operationTask = RunRateTransitionScenarioAsync(operationMetrics);
    var timeout = Task.Delay(TimeSpan.FromSeconds(wasapiSmoke ? 75 : 20));
    var completed = await Task.WhenAny(
        playbackEnded.Task,
        playbackFailed.Task,
        safetyStopRequested.Task,
        timeout);
    if (completed == playbackFailed.Task)
    {
        report.Errors.Add("LibVLC reported a playback error.");
    }
    else if (completed == timeout)
    {
        report.Errors.Add("Timed out waiting for playback to end.");
    }
    else if (completed == safetyStopRequested.Task)
    {
        report.Errors.Add(await safetyStopRequested.Task);
        player.Stop();
        playbackEnded.TrySetResult();
    }
}
await operationTask;
if (runRateTransitions)
    AnalyzeRateTransitions(operationMetrics);
var drainDeadline = Stopwatch.StartNew();
while (buffer.BufferedBytes > 0 && drainDeadline.Elapsed < TimeSpan.FromSeconds(3))
{
    await Task.Delay(10);
}
if (buffer.BufferedBytes > 0)
{
    report.Errors.Add(
        $"Timed out draining {buffer.BufferedBytes} buffered byte(s) after playback ended.");
}
player.Stop();
var consumerCompletion = await Task.WhenAny(consumer, Task.Delay(TimeSpan.FromSeconds(2)));
if (consumerCompletion != consumer) consumerCancellation.Cancel();
var consumerMetrics = await consumer;

report.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
report.CallbackCount = Interlocked.Read(ref callbackCount);
report.InputFrames = Interlocked.Read(ref inputFrames);
report.LimiterOutputFrames = limiter.OutputFrames;
report.BufferedFrames = Interlocked.Read(ref bufferedFrames);
report.MaximumCallbackGapMilliseconds = maximumCallbackGapMilliseconds;
report.Peak = limiter.Peak;
report.MinimumAppliedGain = limiter.MinimumAppliedGain;
report.OverRangeSamples = limiter.OverRangeSamples;
report.NonFiniteInputSamples = limiter.NonFiniteInputSamples;
report.NonFiniteOutputSamples = limiter.NonFiniteOutputSamples;
report.RenderPeak = renderPeak;
report.Consumer = consumerMetrics;
report.Operations = operationMetrics;
report.DrainCallbackCount = Interlocked.Read(ref drainCallbackCount);
report.FlushCallbackCount = Interlocked.Read(ref flushCallbackCount);
report.FlushedBytes = Interlocked.Read(ref flushedBytes);
report.DiscardedLimiterFrames = Interlocked.Read(ref discardedLimiterFrames);
lock (callbackErrors) report.Errors.AddRange(callbackErrors);
report.DiagnosticLogs.AddRange(diagnosticLogs);

if (report.CallbackCount == 0) report.Errors.Add("No PCM callback was received.");
if (report.InputFrames != report.LimiterOutputFrames + report.DiscardedLimiterFrames)
{
    report.Errors.Add(
        $"Limiter frame mismatch: input {report.InputFrames}, output {report.LimiterOutputFrames}, discarded on flush {report.DiscardedLimiterFrames}.");
}
if (report.LimiterOutputFrames != report.BufferedFrames)
{
    report.Errors.Add(
        $"Buffer frame mismatch: limiter {report.LimiterOutputFrames}, buffered {report.BufferedFrames}.");
}
var consumedFrames = consumerMetrics.BytesRead / waveFormat.BlockAlign;
var flushedFrames = report.FlushedBytes / waveFormat.BlockAlign;
if (report.BufferedFrames != consumedFrames + flushedFrames)
{
    report.Errors.Add(
        $"Consumer frame mismatch: buffered {report.BufferedFrames}, consumed {consumedFrames}, flushed {flushedFrames}.");
}
var ceiling = DecibelsToLinear(limiterCeilingDb);
if (report.Peak > ceiling + 0.00001 || report.OverRangeSamples != 0)
{
    report.Errors.Add(
        $"Limiter ceiling failed: peak {report.Peak:0.000000}, over-range {report.OverRangeSamples}.");
}
var expectedRenderCeiling = ceiling * postLimiterGain;
if (report.RenderPeak > expectedRenderCeiling + 0.00001)
{
    report.Errors.Add(
        $"Post-limiter render ceiling failed: peak {report.RenderPeak:0.000000}, expected at most {expectedRenderCeiling:0.000000}.");
}
if (report.NonFiniteInputSamples != 0 || report.NonFiniteOutputSamples != 0)
{
    report.Errors.Add(
        $"Non-finite PCM samples: input {report.NonFiniteInputSamples}, output {report.NonFiniteOutputSamples}.");
}
if (consumerMetrics.OverflowCount != 0)
{
    report.Errors.Add($"Buffer overflow count was {consumerMetrics.OverflowCount}.");
}
if (consumerMetrics.UnderrunCount != 0)
{
    report.Errors.Add($"Post-prebuffer underrun count was {consumerMetrics.UnderrunCount}.");
}
if (!runRateTransitions && report.DrainCallbackCount == 0)
{
    report.Errors.Add("LibVLC did not invoke the drain callback at natural end.");
}
if (!runTransitions && !runRateTransitions && report.FlushedBytes != 0)
{
    report.Errors.Add($"Flush discarded {report.FlushedBytes} queued byte(s).");
}
if (runTransitions)
{
    if (!operationMetrics.PauseCallbackObserved || !operationMetrics.ResumeCallbackObserved)
        report.Errors.Add("Pause/resume audio callbacks were not both observed.");
    if (operationMetrics.CallbacksDuringPause != 0)
        report.Errors.Add($"Received {operationMetrics.CallbacksDuringPause} PCM callback(s) while paused.");
    if (operationMetrics.SeekFlushCallbacks == 0)
        report.Errors.Add("Seek did not invoke an audio flush callback.");
    if (!operationMetrics.Rate15Accepted || !operationMetrics.Rate10Accepted)
        report.Errors.Add("A requested playback rate was rejected.");
    if (operationMetrics.TimeAfterSeekMilliseconds < 4_000)
        report.Errors.Add($"Seek did not reach the expected region: {operationMetrics.TimeAfterSeekMilliseconds} ms.");
}
if (wasapiSmoke)
{
    if (consumerMetrics.LoopbackFrames == 0)
        report.Errors.Add("WASAPI loopback returned no frames.");
    if (consumerMetrics.LoopbackActiveSamples == 0)
        report.Errors.Add("WASAPI loopback returned no active samples.");
    if (consumerMetrics.LoopbackNonFiniteSamples != 0)
        report.Errors.Add($"WASAPI loopback contained {consumerMetrics.LoopbackNonFiniteSamples} non-finite sample(s).");
    if (consumerMetrics.LoopbackPeak > expectedRenderCeiling + 0.00001)
        report.Errors.Add(
            $"WASAPI loopback exceeded the selected attenuation ceiling {expectedRenderCeiling:0.000000}: {consumerMetrics.LoopbackPeak:0.000000}.");
}

report.Passed = report.Errors.Count == 0;
report.FinishedAt = DateTimeOffset.Now;
await File.WriteAllTextAsync(
    outputPath,
    JsonSerializer.Serialize(
        report,
        new JsonSerializerOptions
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        }));

Console.WriteLine($"Passed: {report.Passed}");
Console.WriteLine($"Callbacks: {report.CallbackCount}, max gap: {report.MaximumCallbackGapMilliseconds:0.00} ms");
Console.WriteLine($"Frames: input={report.InputFrames}, limited={report.LimiterOutputFrames}, buffered={report.BufferedFrames}");
Console.WriteLine($"Peak: {report.Peak:0.000000}, over-range: {report.OverRangeSamples}, minimum gain: {report.MinimumAppliedGain:0.0000}");
Console.WriteLine($"Render peak: {report.RenderPeak:0.000000} ({report.PostLimiterSafetyAttenuationDb:0} dB post-limiter)");
Console.WriteLine($"Non-finite: input={report.NonFiniteInputSamples}, output={report.NonFiniteOutputSamples}");
Console.WriteLine($"Buffer: max={consumerMetrics.MaximumBufferedMilliseconds:0.0} ms, underruns={consumerMetrics.UnderrunCount}, overflows={consumerMetrics.OverflowCount}");
if (wasapiSmoke)
    Console.WriteLine($"Loopback: frames={consumerMetrics.LoopbackFrames}, peak={consumerMetrics.LoopbackPeak:0.000000}, active={consumerMetrics.LoopbackActiveSamples}, non-finite={consumerMetrics.LoopbackNonFiniteSamples}");
foreach (var error in report.Errors) Console.WriteLine($"ERROR: {error}");
Console.WriteLine($"Report: {outputPath}");
return report.Passed ? 0 : 1;

void OnAudioPlay(IntPtr data, IntPtr samplesPointer, uint frameCount, long pts)
{
    try
    {
        var now = Stopwatch.GetTimestamp();
        var previous = Interlocked.Exchange(ref lastCallbackTimestamp, now);
        if (previous != 0)
        {
            var gap = Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds;
            maximumCallbackGapMilliseconds = Math.Max(maximumCallbackGapMilliseconds, gap);
        }

        var sampleCount = checked((int)frameCount * channels);
        var pcm16 = new short[sampleCount];
        Marshal.Copy(samplesPointer, pcm16, 0, sampleCount);
        var samples = new float[sampleCount];
        for (var index = 0; index < sampleCount; index++)
        {
            samples[index] = pcm16[index] / 32768f;
        }
        lock (sync)
        {
            var processed = limiter.Process(samples);
            AddToBuffer(processed);
        }
        Interlocked.Increment(ref callbackCount);
        Interlocked.Add(ref inputFrames, frameCount);
        lock (callbackObservationSync)
        {
            callbackObservations.Add(new CallbackObservation
            {
                WallMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                PtsMicroseconds = pts,
                FrameCount = frameCount,
                RequestedRate = Volatile.Read(ref requestedRateMilli) / 1_000f,
            });
        }
        firstBuffered.TrySetResult();
    }
    catch (Exception exception)
    {
        lock (callbackErrors) callbackErrors.Add($"Play callback: {exception.Message}");
    }
}

void AddToBuffer(float[] samples)
{
    if (samples.Length == 0) return;
    var renderGain = (float)postLimiterGain;
    if (renderGain != 1f)
    {
        samples = (float[])samples.Clone();
        for (var index = 0; index < samples.Length; index++) samples[index] *= renderGain;
    }
    foreach (var sample in samples) renderPeak = Math.Max(renderPeak, Math.Abs((double)sample));
    var bytes = new byte[samples.Length * sizeof(float)];
    Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
    try
    {
        buffer.AddSamples(bytes, 0, bytes.Length);
        Interlocked.Add(ref bufferedFrames, samples.Length / channels);
    }
    catch (InvalidOperationException)
    {
        consumerMetricsOverflowIncrement();
        throw;
    }
}

void consumerMetricsOverflowIncrement()
{
    // Overflow is also captured as a callback error. The consumer result derives its count
    // from the shared counter below so the report keeps one authoritative value.
    Interlocked.Increment(ref overflowCount);
}

async Task<ConsumerMetrics> ConsumeBufferAsync(
    BufferedWaveProvider provider,
    Task firstData,
    Task ended,
    CancellationToken cancellationToken)
{
    var metrics = new ConsumerMetrics { Mode = "simulated" };
    try
    {
        await firstData.WaitAsync(cancellationToken);
        var prebufferBytes = waveFormat.ConvertLatencyToByteSize(prebufferMilliseconds);
        while (provider.BufferedBytes < prebufferBytes && !ended.IsCompleted)
        {
            await Task.Delay(2, cancellationToken);
        }

        var periodBytes = waveFormat.ConvertLatencyToByteSize(consumerPeriodMilliseconds);
        var readBuffer = new byte[periodBytes];
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(consumerPeriodMilliseconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            metrics.MaximumBufferedMilliseconds = Math.Max(
                metrics.MaximumBufferedMilliseconds,
                provider.BufferedDuration.TotalMilliseconds);
            var read = provider.Read(readBuffer, 0, readBuffer.Length);
            metrics.ReadCalls++;
            metrics.BytesRead += read;
            if (read < readBuffer.Length &&
                !ended.IsCompleted &&
                Interlocked.Read(ref isDraining) == 0)
            {
                metrics.UnderrunCount++;
            }
            if ((ended.IsCompleted || Interlocked.Read(ref isDraining) != 0) &&
                provider.BufferedBytes == 0)
            {
                break;
            }
        }
    }
    catch (OperationCanceledException)
    {
    }
    finally
    {
        metrics.OverflowCount = Interlocked.Read(ref overflowCount);
    }
    return metrics;
}

async Task<ConsumerMetrics> ConsumeWasapiAsync(
    BufferedWaveProvider provider,
    Task firstData,
    Task ended,
    CancellationToken cancellationToken)
{
    var metrics = new ConsumerMetrics { Mode = "wasapi-smoke" };
    CountingWaveProvider? countingProvider = null;
    try
    {
        await firstData.WaitAsync(cancellationToken);
        var prebufferBytes = waveFormat.ConvertLatencyToByteSize(prebufferMilliseconds);
        while (provider.BufferedBytes < prebufferBytes && !ended.IsCompleted)
            await Task.Delay(2, cancellationToken);

        using var enumerator = new MMDeviceEnumerator();
        using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var currentVolume = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
        var currentMuted = endpoint.AudioEndpointVolume.Mute;
        metrics.MaximumObservedEndpointVolume = currentVolume;
        if (currentVolume > maximumEndpointVolumeScalar + 0.000001 && !currentMuted)
            throw new InvalidOperationException(
                $"Safety guard blocked playback immediately before WASAPI start: volume is {currentVolume:P1}.");

        using var capture = new WasapiLoopbackCapture(endpoint);
        using var output = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 50);
        countingProvider = new CountingWaveProvider(provider, padWithSilence: true, sync);
        var captureStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.DataAvailable += (_, eventArgs) => AnalyzeLoopback(eventArgs.Buffer, eventArgs.BytesRecorded, capture.WaveFormat, metrics);
        capture.RecordingStopped += (_, eventArgs) =>
        {
            if (eventArgs.Exception is not null) metrics.CaptureError = eventArgs.Exception.Message;
            captureStopped.TrySetResult();
        };
        output.PlaybackStopped += (_, eventArgs) =>
        {
            if (eventArgs.Exception is not null) metrics.PlaybackError = eventArgs.Exception.Message;
        };

        output.Init(countingProvider);
        capture.StartRecording();
        output.Play();
        var wasEmpty = false;
        var outputPaused = false;
        var rebufferingAfterFlush = false;
        var observedFlushCount = Interlocked.Read(ref flushCallbackCount);
        var lastSafetyCheck = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            var currentFlushCount = Interlocked.Read(ref flushCallbackCount);
            if (currentFlushCount != observedFlushCount && !ended.IsCompleted)
            {
                observedFlushCount = currentFlushCount;
                rebufferingAfterFlush = true;
            }
            if (rebufferingAfterFlush && provider.BufferedBytes >= prebufferBytes)
                rebufferingAfterFlush = false;

            var pauseRequested = Interlocked.Read(ref audioPaused) != 0 || rebufferingAfterFlush;
            if (pauseRequested != outputPaused)
            {
                if (pauseRequested) output.Pause(); else output.Play();
                outputPaused = pauseRequested;
            }
            if (lastSafetyCheck.ElapsedMilliseconds >= 100)
            {
                lastSafetyCheck.Restart();
                var monitoredVolume = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
                var monitoredMute = endpoint.AudioEndpointVolume.Mute;
                metrics.MaximumObservedEndpointVolume = Math.Max(
                    metrics.MaximumObservedEndpointVolume,
                    monitoredVolume);
                if (monitoredVolume > maximumEndpointVolumeScalar + 0.000001 && !monitoredMute)
                {
                    metrics.SafetyStopReason =
                        $"Safety guard stopped playback because default endpoint volume changed to {monitoredVolume:P1}.";
                    safetyStopRequested.TrySetResult(metrics.SafetyStopReason);
                    break;
                }
            }
            metrics.MaximumBufferedMilliseconds = Math.Max(
                metrics.MaximumBufferedMilliseconds,
                provider.BufferedDuration.TotalMilliseconds);
            var empty = provider.BufferedBytes == 0;
            if (empty && !wasEmpty && !ended.IsCompleted &&
                Interlocked.Read(ref isDraining) == 0 &&
                Interlocked.Read(ref audioPaused) == 0 &&
                !rebufferingAfterFlush)
                metrics.UnderrunCount++;
            wasEmpty = empty;
            if ((ended.IsCompleted || Interlocked.Read(ref isDraining) != 0) && empty) break;
            await Task.Delay(2, cancellationToken);
        }
        await Task.Delay(100);
        output.Stop();
        capture.StopRecording();
        await captureStopped.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        if (!string.IsNullOrEmpty(metrics.PlaybackError))
            throw new InvalidOperationException($"WASAPI playback failed: {metrics.PlaybackError}");
        if (!string.IsNullOrEmpty(metrics.CaptureError))
            throw new InvalidOperationException($"WASAPI capture failed: {metrics.CaptureError}");
    }
    catch (OperationCanceledException)
    {
    }
    catch (Exception exception)
    {
        lock (callbackErrors) callbackErrors.Add($"WASAPI consumer: {exception.Message}");
    }
    finally
    {
        if (countingProvider is not null)
        {
            metrics.BytesRead = countingProvider.BytesRead;
            metrics.ReadCalls = countingProvider.ReadCalls;
        }
        metrics.OverflowCount = Interlocked.Read(ref overflowCount);
    }
    return metrics;
}

async Task RunTransitionScenarioAsync(OperationMetrics metrics)
{
    await firstBuffered.Task;
    await Task.Delay(1_000);

    var callbacksBeforePause = Interlocked.Read(ref callbackCount);
    player.SetPause(true);
    var pauseDeadline = Stopwatch.StartNew();
    while (Interlocked.Read(ref audioPaused) == 0 && pauseDeadline.Elapsed < TimeSpan.FromSeconds(1))
        await Task.Delay(5);
    metrics.PauseCallbackObserved = Interlocked.Read(ref audioPaused) != 0;
    await Task.Delay(750);
    metrics.CallbacksDuringPause = Interlocked.Read(ref callbackCount) - callbacksBeforePause;

    player.SetPause(false);
    var resumeDeadline = Stopwatch.StartNew();
    while (Interlocked.Read(ref audioPaused) != 0 && resumeDeadline.Elapsed < TimeSpan.FromSeconds(1))
        await Task.Delay(5);
    metrics.ResumeCallbackObserved = Interlocked.Read(ref audioPaused) == 0;
    await Task.Delay(750);

    var flushesBeforeSeek = Interlocked.Read(ref flushCallbackCount);
    player.Position = 0.5f;
    await Task.Delay(1_000);
    metrics.SeekFlushCallbacks = Interlocked.Read(ref flushCallbackCount) - flushesBeforeSeek;
    metrics.TimeAfterSeekMilliseconds = player.Time;

    metrics.Rate15Accepted = player.SetRate(1.5f) == 0;
    await Task.Delay(1_000);
    metrics.ObservedRate15 = player.Rate;
    metrics.Rate10Accepted = player.SetRate(1f) == 0;
    await Task.Delay(250);
    metrics.ObservedRate10 = player.Rate;
}

async Task RunRateTransitionScenarioAsync(OperationMetrics metrics)
{
    await firstBuffered.Task;
    await Task.Delay(1_000);

    foreach (var rate in new[] { 0.25f, 1f, 0.5f, 1f, 2f, 1f })
    {
        var transition = new RateTransitionMetric
        {
            FromRate = Volatile.Read(ref requestedRateMilli) / 1_000f,
            ToRate = rate,
            RequestWallMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            MediaTimeAtRequestMilliseconds = player.Time,
        };
        Volatile.Write(ref requestedRateMilli, (long)Math.Round(rate * 1_000));
        transition.Accepted = player.SetRate(rate) == 0;
        await Task.Delay(750);
        transition.ObservedRate = player.Rate;
        metrics.RateTransitions.Add(transition);
    }

    player.Stop();
    playbackEnded.TrySetResult();
}

void AnalyzeRateTransitions(OperationMetrics metrics)
{
    CallbackObservation[] observations;
    lock (callbackObservationSync)
        observations = callbackObservations.ToArray();

    foreach (var transition in metrics.RateTransitions)
    {
        var firstAfter = observations.FirstOrDefault(
            observation => observation.WallMilliseconds >= transition.RequestWallMilliseconds);
        if (firstAfter is not null)
            transition.FirstCallbackDelayMilliseconds =
                firstAfter.WallMilliseconds - transition.RequestWallMilliseconds;

        for (var index = 1; index < observations.Length; index++)
        {
            var previous = observations[index - 1];
            var current = observations[index];
            if (current.WallMilliseconds < transition.RequestWallMilliseconds ||
                current.WallMilliseconds > transition.RequestWallMilliseconds + 750)
                continue;

            transition.MaximumCallbackGapMilliseconds = Math.Max(
                transition.MaximumCallbackGapMilliseconds,
                current.WallMilliseconds - previous.WallMilliseconds);
            var expectedPts = previous.PtsMicroseconds +
                previous.FrameCount * 1_000_000d / sampleRate;
            var ptsDelta = current.PtsMicroseconds - expectedPts;
            transition.MaximumPositivePtsDeltaMicroseconds = Math.Max(
                transition.MaximumPositivePtsDeltaMicroseconds,
                ptsDelta);
            transition.MinimumPtsDeltaMicroseconds = Math.Min(
                transition.MinimumPtsDeltaMicroseconds,
                ptsDelta);
        }
    }
}

static void AnalyzeLoopback(byte[] bytes, int byteCount, WaveFormat format, ConsumerMetrics metrics)
{
    if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32)
    {
        metrics.CaptureError = $"Unsupported loopback format: {format}.";
        return;
    }
    var samples = new float[byteCount / sizeof(float)];
    Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * sizeof(float));
    metrics.LoopbackFrames += samples.Length / format.Channels;
    foreach (var sample in samples)
    {
        if (!float.IsFinite(sample))
        {
            metrics.LoopbackNonFiniteSamples++;
            continue;
        }
        var absolute = Math.Abs((double)sample);
        metrics.LoopbackPeak = Math.Max(metrics.LoopbackPeak, absolute);
        if (absolute > 1) metrics.LoopbackOverRangeSamples++;
        if (absolute > 0.000001) metrics.LoopbackActiveSamples++;
    }
}

static double DecibelsToLinear(double decibels) => Math.Pow(10, decibels / 20);

static bool IsRelevantLibVlcLog(string message)
{
    return message.Contains("scaletempo", StringComparison.OrdinalIgnoreCase) ||
           message.Contains("too late", StringComparison.OrdinalIgnoreCase) ||
           message.Contains("too early", StringComparison.OrdinalIgnoreCase) ||
           message.Contains("insert", StringComparison.OrdinalIgnoreCase) ||
           message.Contains("flush", StringComparison.OrdinalIgnoreCase) ||
           message.Contains("resampl", StringComparison.OrdinalIgnoreCase);
}

internal sealed class StreamingLookaheadLimiter
{
    private readonly int _channels;
    private readonly double _boost;
    private readonly double _ceiling;
    private readonly double _releaseCoefficient;
    private readonly int _lookaheadFrames;
    private readonly float[] _pending;
    private readonly long[] _pendingIndices;
    private readonly long[] _peakIndices;
    private readonly double[] _peakValues;
    private int _pendingHead;
    private int _pendingCount;
    private int _peakHead;
    private int _peakCount;
    private long _nextIndex;
    private double _smoothedGain = 1;

    public StreamingLookaheadLimiter(
        int channels,
        int sampleRate,
        double boost,
        double ceiling,
        double lookaheadMilliseconds,
        double releaseMilliseconds)
    {
        _channels = channels;
        _boost = boost;
        _ceiling = ceiling;
        _lookaheadFrames = Math.Max(1, (int)Math.Round(sampleRate * lookaheadMilliseconds / 1000));
        _releaseCoefficient = Math.Exp(-1 / (sampleRate * releaseMilliseconds / 1000));
        var capacity = _lookaheadFrames + 1;
        _pending = new float[capacity * channels];
        _pendingIndices = new long[capacity];
        _peakIndices = new long[capacity];
        _peakValues = new double[capacity];
    }

    public long OutputFrames { get; private set; }
    public int PendingFrames => _pendingCount;
    public double Peak { get; private set; }
    public long OverRangeSamples { get; private set; }
    public long NonFiniteInputSamples { get; private set; }
    public long NonFiniteOutputSamples { get; private set; }
    public double MinimumAppliedGain { get; private set; } = 1;

    public float[] Process(float[] input)
    {
        if (input.Length % _channels != 0) throw new ArgumentException("Incomplete PCM frame.");
        var output = new float[input.Length];
        var outputSamples = 0;
        for (var offset = 0; offset < input.Length; offset += _channels)
        {
            AddFrame(input, offset);
            if (_pendingCount > _lookaheadFrames)
            {
                outputSamples += EmitFrame(output, outputSamples);
            }
        }
        if (outputSamples == output.Length) return output;
        Array.Resize(ref output, outputSamples);
        return output;
    }

    public float[] Flush()
    {
        var output = new float[_pendingCount * _channels];
        var outputSamples = 0;
        while (_pendingCount > 0) outputSamples += EmitFrame(output, outputSamples);
        return output;
    }

    public void Reset()
    {
        _pendingHead = 0;
        _pendingCount = 0;
        _peakHead = 0;
        _peakCount = 0;
        _nextIndex = 0;
        _smoothedGain = 1;
    }

    private void AddFrame(float[] input, int offset)
    {
        var capacity = _pendingIndices.Length;
        var slot = (_pendingHead + _pendingCount) % capacity;
        var index = _nextIndex++;
        double peak = 0;
        for (var channel = 0; channel < _channels; channel++)
        {
            var inputValue = input[offset + channel];
            if (!float.IsFinite(inputValue))
            {
                NonFiniteInputSamples++;
                inputValue = 0;
            }
            var value = (float)(inputValue * _boost);
            _pending[slot * _channels + channel] = value;
            peak = Math.Max(peak, Math.Abs((double)value));
        }
        _pendingIndices[slot] = index;
        _pendingCount++;

        while (_peakCount > 0)
        {
            var last = (_peakHead + _peakCount - 1) % capacity;
            if (_peakValues[last] > peak) break;
            _peakCount--;
        }
        var peakSlot = (_peakHead + _peakCount) % capacity;
        _peakIndices[peakSlot] = index;
        _peakValues[peakSlot] = peak;
        _peakCount++;
    }

    private int EmitFrame(float[] output, int outputOffset)
    {
        var capacity = _pendingIndices.Length;
        var index = _pendingIndices[_pendingHead];
        var lookaheadPeak = _peakValues[_peakHead];
        var requiredGain = lookaheadPeak > _ceiling ? _ceiling / lookaheadPeak : 1;
        _smoothedGain = requiredGain < _smoothedGain
            ? requiredGain
            : requiredGain + _releaseCoefficient * (_smoothedGain - requiredGain);
        MinimumAppliedGain = Math.Min(MinimumAppliedGain, _smoothedGain);

        for (var channel = 0; channel < _channels; channel++)
        {
            var value = _pending[_pendingHead * _channels + channel] * _smoothedGain;
            value = Math.Clamp(value, -_ceiling, _ceiling);
            if (!double.IsFinite(value))
            {
                NonFiniteOutputSamples++;
                value = 0;
            }
            output[outputOffset + channel] = (float)value;
            Peak = Math.Max(Peak, Math.Abs(value));
            if (Math.Abs(value) > 1) OverRangeSamples++;
        }
        OutputFrames++;
        _pendingHead = (_pendingHead + 1) % capacity;
        _pendingCount--;
        if (_peakCount > 0 && _peakIndices[_peakHead] <= index)
        {
            _peakHead = (_peakHead + 1) % capacity;
            _peakCount--;
        }
        return _channels;
    }
}

internal sealed class ProbeReport
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public bool Passed { get; set; }
    public string MediaPath { get; set; } = "";
    public int VolumePercent { get; set; }
    public string Mode { get; set; } = "";
    public double PostLimiterSafetyAttenuationDb { get; set; }
    public double MaximumEndpointVolumePercent { get; set; }
    public string OperationScenario { get; set; } = "";
    public string TimeStretchProfile { get; set; } = "default";
    public string Safety { get; set; } = "";
    public EndpointInfo? DefaultEndpoint { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public long CallbackCount { get; set; }
    public long InputFrames { get; set; }
    public long LimiterOutputFrames { get; set; }
    public long BufferedFrames { get; set; }
    public double MaximumCallbackGapMilliseconds { get; set; }
    public double Peak { get; set; }
    public double MinimumAppliedGain { get; set; }
    public long OverRangeSamples { get; set; }
    public long NonFiniteInputSamples { get; set; }
    public long NonFiniteOutputSamples { get; set; }
    public double RenderPeak { get; set; }
    public ConsumerMetrics Consumer { get; set; } = new();
    public OperationMetrics Operations { get; set; } = new();
    public long DrainCallbackCount { get; set; }
    public long FlushCallbackCount { get; set; }
    public long FlushedBytes { get; set; }
    public long DiscardedLimiterFrames { get; set; }
    public List<string> Warnings { get; } = [];
    public List<string> DiagnosticLogs { get; } = [];
    public List<string> Errors { get; } = [];
}

internal sealed class OperationMetrics
{
    public string Scenario { get; set; } = "none";
    public bool PauseCallbackObserved { get; set; }
    public bool ResumeCallbackObserved { get; set; }
    public long CallbacksDuringPause { get; set; }
    public long SeekFlushCallbacks { get; set; }
    public long TimeAfterSeekMilliseconds { get; set; }
    public bool Rate15Accepted { get; set; }
    public float ObservedRate15 { get; set; }
    public bool Rate10Accepted { get; set; }
    public float ObservedRate10 { get; set; }
    public List<RateTransitionMetric> RateTransitions { get; } = [];
}

internal sealed class RateTransitionMetric
{
    public float FromRate { get; set; }
    public float ToRate { get; set; }
    public double RequestWallMilliseconds { get; set; }
    public long MediaTimeAtRequestMilliseconds { get; set; }
    public bool Accepted { get; set; }
    public float ObservedRate { get; set; }
    public double FirstCallbackDelayMilliseconds { get; set; }
    public double MaximumCallbackGapMilliseconds { get; set; }
    public double MaximumPositivePtsDeltaMicroseconds { get; set; }
    public double MinimumPtsDeltaMicroseconds { get; set; }
}

internal sealed class CallbackObservation
{
    public double WallMilliseconds { get; set; }
    public long PtsMicroseconds { get; set; }
    public uint FrameCount { get; set; }
    public float RequestedRate { get; set; }
}

internal sealed class EndpointInfo
{
    public string FriendlyName { get; set; } = "";
    public string Id { get; set; } = "";
    public string State { get; set; } = "";
    public string MixFormat { get; set; } = "";
    public double MasterVolumeScalar { get; set; }
    public bool Muted { get; set; }
}

internal sealed class ConsumerMetrics
{
    public string Mode { get; set; } = "";
    public long ReadCalls { get; set; }
    public long BytesRead { get; set; }
    public long UnderrunCount { get; set; }
    public long OverflowCount { get; set; }
    public double MaximumBufferedMilliseconds { get; set; }
    public long LoopbackFrames { get; set; }
    public long LoopbackActiveSamples { get; set; }
    public double LoopbackPeak { get; set; }
    public long LoopbackNonFiniteSamples { get; set; }
    public long LoopbackOverRangeSamples { get; set; }
    public string? PlaybackError { get; set; }
    public string? CaptureError { get; set; }
    public double MaximumObservedEndpointVolume { get; set; }
    public string? SafetyStopReason { get; set; }
}

internal sealed class CountingWaveProvider(
    IWaveProvider source,
    bool padWithSilence = false,
    object? syncLock = null) : IWaveProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;
    public long ReadCalls { get; private set; }
    public long BytesRead { get; private set; }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (syncLock is null) return ReadCore(buffer, offset, count);
        lock (syncLock) return ReadCore(buffer, offset, count);
    }

    private int ReadCore(byte[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        ReadCalls++;
        BytesRead += read;
        if (!padWithSilence || read == count) return read;
        Array.Clear(buffer, offset + read, count - read);
        return count;
    }
}

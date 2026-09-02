using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BakuretsuOsakanaKobo.Playback;

internal sealed class RealtimeAudioOutput : IDisposable
{
    private static readonly TimeSpan PrebufferDuration = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RebufferThreshold = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);
    private const int WasapiLatencyMilliseconds = 20;

    private readonly object _sync = new();
    private readonly Action<Exception> _failureHandler;
    private readonly RealtimeAudioPipeline _pipeline = new();
    private readonly AutoResetEvent _stateChanged = new(false);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread _renderThread;
    private readonly MediaPlayer.LibVLCAudioPlayCb _playCallback;
    private readonly MediaPlayer.LibVLCAudioPauseCb _pauseCallback;
    private readonly MediaPlayer.LibVLCAudioResumeCb _resumeCallback;
    private readonly MediaPlayer.LibVLCAudioFlushCb _flushCallback;
    private readonly MediaPlayer.LibVLCAudioDrainCb _drainCallback;
    private bool _isPaused;
    private bool _isDraining;
    private bool _isRebuffering = true;
    private bool _acceptCallbacks = true;
    private bool _disposed;
    private long _callbackCount;
    private long _inputFrames;
    private long _bufferedFrames;
    private long _consumedFrames;
    private long _underrunCount;
    private long _overflowCount;
    private long _flushCount;
    private long _drainCount;
    private long _completedDrainCount;
    private long _flushedBytes;
    private long _discardedLimiterFrames;
    private long _discardedRawFrames;
    private long _pauseCallbackCount;
    private long _resumeCallbackCount;
    private bool _sessionNormalized;
    private float _sessionVolumeBeforeNormalization = 1;
    private bool _sessionMutedBeforeNormalization;
    private long _failureReported;
    private long _outputStarted;
    private long _levelChangeCount;
    private int _lastLevelChangeBufferedBytes;

    public RealtimeAudioOutput(Action<Exception> failureHandler)
    {
        _failureHandler = failureHandler;
        _playCallback = OnAudioPlay;
        _pauseCallback = OnAudioPause;
        _resumeCallback = OnAudioResume;
        _flushCallback = OnAudioFlush;
        _drainCallback = OnAudioDrain;
        _renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "BakuretsuOsakanaKobo audio render",
        };
        _renderThread.Start();
    }

    public int VolumePercent
    {
        get
        {
            lock (_sync)
            {
                return _pipeline.VolumePercent;
            }
        }
    }

    public bool IsMuted
    {
        get
        {
            lock (_sync)
            {
                return _pipeline.IsMuted;
            }
        }
    }

    public RealtimeAudioDiagnostics Diagnostics
    {
        get
        {
            lock (_sync)
            {
                return new RealtimeAudioDiagnostics(
                    Interlocked.Read(ref _callbackCount),
                    Interlocked.Read(ref _inputFrames),
                    Interlocked.Read(ref _bufferedFrames),
                    Interlocked.Read(ref _consumedFrames),
                    Interlocked.Read(ref _underrunCount),
                    Interlocked.Read(ref _overflowCount),
                    Interlocked.Read(ref _flushCount),
                    Interlocked.Read(ref _drainCount),
                    Interlocked.Read(ref _completedDrainCount),
                    Interlocked.Read(ref _flushedBytes),
                    Interlocked.Read(ref _discardedLimiterFrames),
                    Interlocked.Read(ref _discardedRawFrames),
                    Interlocked.Read(ref _pauseCallbackCount),
                    Interlocked.Read(ref _resumeCallbackCount),
                    _pipeline.PendingFrames,
                    _pipeline.NonFiniteInputSamples,
                    _pipeline.NonFiniteOutputSamples,
                    _pipeline.Peak,
                    _pipeline.OverRangeSamples,
                    _isPaused,
                    _isRebuffering,
                    _pipeline.RawBufferedBytes,
                    _sessionNormalized,
                    _sessionVolumeBeforeNormalization,
                    _sessionMutedBeforeNormalization,
                    Interlocked.Read(ref _outputStarted) != 0,
                    Interlocked.Read(ref _failureReported) != 0,
                    _renderThread.IsAlive,
                    Interlocked.Read(ref _levelChangeCount),
                    _lastLevelChangeBufferedBytes,
                    BufferedMilliseconds(_lastLevelChangeBufferedBytes));
            }
        }
    }

    public void AttachTo(MediaPlayer mediaPlayer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        mediaPlayer.SetAudioCallbacks(
            _playCallback,
            _pauseCallback,
            _resumeCallback,
            _flushCallback,
            _drainCallback);
        mediaPlayer.SetAudioFormat(
            "S16N",
            RealtimeVolumeProcessor.SampleRate,
            RealtimeVolumeProcessor.Channels);
    }

    public void SetVolumePercent(int volumePercent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _pipeline.SetVolumePercent(volumePercent);
            RecordLevelChangeBufferDepth();
        }
    }

    public void SetMuted(bool isMuted)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _pipeline.SetMuted(isMuted);
            RecordLevelChangeBufferDepth();
        }
    }

    public void PrepareForPlayback()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _isPaused = false;
        }

        _stateChanged.Set();
    }

    public void PrepareForRateChange()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            ResetBufferedAudio();
        }

        _stateChanged.Set();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_sync)
        {
            _acceptCallbacks = false;
            _disposed = true;
        }

        _shutdown.Cancel();
        _stateChanged.Set();
        if (!_renderThread.Join(TimeSpan.FromSeconds(5)))
        {
            ReportFailure(new TimeoutException("The WASAPI render thread did not stop within five seconds."));
            return;
        }

        _shutdown.Dispose();
        _stateChanged.Dispose();
    }

    private void OnAudioPlay(IntPtr data, IntPtr samplesPointer, uint frameCount, long pts)
    {
        try
        {
            if (!CanAcceptCallbacks())
            {
                return;
            }

            var sampleCount = checked((int)frameCount * RealtimeVolumeProcessor.Channels);
            var pcm16 = new short[sampleCount];
            Marshal.Copy(samplesPointer, pcm16, 0, sampleCount);
            lock (_sync)
            {
                if (!_acceptCallbacks || Interlocked.Read(ref _failureReported) != 0)
                {
                    return;
                }

                try
                {
                    _pipeline.AddPcm16(pcm16);
                }
                catch (InvalidOperationException exception)
                {
                    Interlocked.Increment(ref _overflowCount);
                    throw new InvalidOperationException("The bounded raw PCM buffer overflowed.", exception);
                }
                // LibVLC can begin a new playback sequence with play callbacks without a
                // matching resume callback for the pause notification from the old sequence.
                // Receiving samples that are ready for the output is authoritative evidence
                // that rendering may continue.
                _isPaused = false;
                _isDraining = false;
            }

            Interlocked.Increment(ref _callbackCount);
            Interlocked.Add(ref _inputFrames, frameCount);
            _stateChanged.Set();
        }
        catch (Exception exception)
        {
            ReportFailure(new InvalidOperationException("The LibVLC PCM play callback failed.", exception));
        }
    }

    private void OnAudioPause(IntPtr data, long pts)
    {
        try
        {
            Interlocked.Increment(ref _pauseCallbackCount);
            lock (_sync)
            {
                _isPaused = true;
            }

            _stateChanged.Set();
        }
        catch (Exception exception)
        {
            ReportFailure(new InvalidOperationException("The LibVLC PCM pause callback failed.", exception));
        }
    }

    private void OnAudioResume(IntPtr data, long pts)
    {
        try
        {
            Interlocked.Increment(ref _resumeCallbackCount);
            lock (_sync)
            {
                _isPaused = false;
            }

            _stateChanged.Set();
        }
        catch (Exception exception)
        {
            ReportFailure(new InvalidOperationException("The LibVLC PCM resume callback failed.", exception));
        }
    }

    private void OnAudioFlush(IntPtr data, long pts)
    {
        try
        {
            lock (_sync)
            {
                ResetBufferedAudio();
            }

            _stateChanged.Set();
        }
        catch (Exception exception)
        {
            ReportFailure(new InvalidOperationException("The LibVLC PCM flush callback failed.", exception));
        }
    }

    private void OnAudioDrain(IntPtr data)
    {
        try
        {
            Interlocked.Increment(ref _drainCount);
            lock (_sync)
            {
                _pipeline.BeginDrain();
                _isDraining = true;
                _isRebuffering = false;
            }

            _stateChanged.Set();
            var deadline = DateTime.UtcNow + DrainTimeout;
            while (!IsDrainComplete() && DateTime.UtcNow < deadline && !_shutdown.IsCancellationRequested)
            {
                Thread.Sleep(2);
            }

            if (!IsDrainComplete() && !_shutdown.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The PCM drain callback timed out with {GetDrainBufferedBytes()} buffered byte(s).");
            }

            Interlocked.Increment(ref _completedDrainCount);
        }
        catch (Exception exception)
        {
            ReportFailure(new InvalidOperationException("The LibVLC PCM drain callback failed.", exception));
        }
    }

    private void RenderLoop()
    {
        MMDeviceEnumerator? enumerator = null;
        MMDevice? endpoint = null;
        WasapiOut? output = null;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                _stateChanged.WaitOne(10);
                if (_shutdown.IsCancellationRequested || Interlocked.Read(ref _failureReported) != 0)
                {
                    break;
                }

                bool shouldStart;
                bool shouldPause;
                lock (_sync)
                {
                    if (_isRebuffering &&
                        (_pipeline.RawBufferedDuration >= PrebufferDuration || (_isDraining && _pipeline.DrainBufferedBytes > 0)))
                    {
                        _isRebuffering = false;
                    }

                    shouldStart = !_isPaused && !_isRebuffering &&
                        (_isDraining ? _pipeline.DrainBufferedBytes > 0 : _pipeline.PlaybackBufferedBytes > 0);
                    shouldPause = !shouldStart;
                }

                if (output is null && shouldStart)
                {
                    enumerator = new MMDeviceEnumerator();
                    endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    output = new WasapiOut(
                        endpoint,
                        AudioClientShareMode.Shared,
                        true,
                        WasapiLatencyMilliseconds);
                    output.PlaybackStopped += Output_OnPlaybackStopped;
                    output.Init(new LockedPaddedWaveProvider(this));
                    NormalizeApplicationAudioSession(endpoint);
                    Interlocked.Exchange(ref _outputStarted, 1);
                }

                if (output is null)
                {
                    continue;
                }

                if (shouldStart && output.PlaybackState != PlaybackState.Playing)
                {
                    output.Play();
                }
                else if (shouldPause && output.PlaybackState == PlaybackState.Playing)
                {
                    output.Pause();
                }

                lock (_sync)
                {
                    if (_isDraining && _pipeline.IsDrainComplete)
                    {
                        _isDraining = false;
                        _isRebuffering = true;
                    }
                    else if (!_isPaused && !_isRebuffering && _pipeline.PlaybackBufferedBytes == 0)
                    {
                        _isRebuffering = true;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            ReportFailure(new InvalidOperationException("The WASAPI render loop failed.", exception));
        }
        finally
        {
            if (output is not null)
            {
                output.PlaybackStopped -= Output_OnPlaybackStopped;
                try
                {
                    output.Stop();
                }
                catch (Exception exception)
                {
                    ReportFailure(exception);
                }

                output.Dispose();
            }

            endpoint?.Dispose();
            enumerator?.Dispose();
        }
    }

    private void Output_OnPlaybackStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (eventArgs.Exception is not null && !_shutdown.IsCancellationRequested)
        {
            ReportFailure(new InvalidOperationException("WASAPI playback stopped unexpectedly.", eventArgs.Exception));
        }
    }

    private void NormalizeApplicationAudioSession(MMDevice endpoint)
    {
        var sessionManager = endpoint.AudioSessionManager;
        try
        {
            using var sessionVolume = sessionManager.SimpleAudioVolume;
            var volumeBeforeNormalization = sessionVolume.Volume;
            var mutedBeforeNormalization = sessionVolume.Mute;

            // Volume and mute are implemented by the application's PCM processor. Keep the
            // process-default WASAPI session neutral so a stale Windows per-app mixer state
            // cannot contradict the in-app controls and silently discard the processed audio.
            sessionVolume.Volume = 1;
            sessionVolume.Mute = false;
            lock (_sync)
            {
                _sessionVolumeBeforeNormalization = volumeBeforeNormalization;
                _sessionMutedBeforeNormalization = mutedBeforeNormalization;
                _sessionNormalized = true;
            }
        }
        finally
        {
            sessionManager.Dispose();
        }
    }

    private void ResetBufferedAudio()
    {
        Interlocked.Increment(ref _flushCount);
        var reset = _pipeline.Reset();
        Interlocked.Add(ref _flushedBytes, reset.DiscardedProcessedBytes);
        Interlocked.Add(ref _discardedLimiterFrames, reset.DiscardedLimiterFrames);
        Interlocked.Add(ref _discardedRawFrames, reset.DiscardedRawFrames);
        _isDraining = false;
        _isRebuffering = true;
    }

    private void RecordLevelChangeBufferDepth()
    {
        _lastLevelChangeBufferedBytes = _pipeline.RawBufferedBytes;
        Interlocked.Increment(ref _levelChangeCount);
    }

    internal static double BufferedMilliseconds(int bufferedBytes)
    {
        var bytesPerFrame = sizeof(float) * RealtimeVolumeProcessor.Channels;
        return Math.Max(0, bufferedBytes) * 1000.0 /
            bytesPerFrame /
            RealtimeVolumeProcessor.SampleRate;
    }

    internal static bool IsBelowRebufferThreshold(int bufferedBytes) =>
        BufferedMilliseconds(bufferedBytes) < RebufferThreshold.TotalMilliseconds;

    private int ReadForRender(byte[] destination, int offset, int count)
    {
        lock (_sync)
        {
            if (_isRebuffering && !_isDraining)
            {
                Array.Clear(destination, offset, count);
                return count;
            }

            var read = _pipeline.Read(destination, offset, count, _isDraining, out var generatedFrames);
            Interlocked.Add(ref _bufferedFrames, generatedFrames);
            Interlocked.Add(
                ref _consumedFrames,
                read / _pipeline.WaveFormat.BlockAlign);
            if (read < count)
            {
                Array.Clear(destination, offset + read, count - read);
                if (!_isPaused && !_isRebuffering && !_isDraining)
                {
                    Interlocked.Increment(ref _underrunCount);
                    _isRebuffering = true;
                    _stateChanged.Set();
                }
            }
            else if (!_isPaused && !_isRebuffering && !_isDraining &&
                IsBelowRebufferThreshold(_pipeline.PlaybackBufferedBytes))
            {
                _isRebuffering = true;
                _stateChanged.Set();
            }

            return count;
        }
    }

    private bool IsDrainComplete()
    {
        lock (_sync)
        {
            return _pipeline.IsDrainComplete;
        }
    }

    private int GetDrainBufferedBytes()
    {
        lock (_sync)
        {
            return _pipeline.DrainBufferedBytes;
        }
    }

    private bool CanAcceptCallbacks()
    {
        lock (_sync)
        {
            return _acceptCallbacks && Interlocked.Read(ref _failureReported) == 0;
        }
    }

    private void ReportFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref _failureReported, 1) != 0)
        {
            return;
        }

        try
        {
            _failureHandler(exception);
        }
        catch
        {
            // Exceptions must never cross a LibVLC or NAudio callback boundary.
        }

        _stateChanged.Set();
    }

    private sealed class LockedPaddedWaveProvider(RealtimeAudioOutput owner) : IWaveProvider
    {
        public WaveFormat WaveFormat => owner._pipeline.WaveFormat;

        public int Read(byte[] buffer, int offset, int count) =>
            owner.ReadForRender(buffer, offset, count);
    }
}

internal readonly record struct RealtimeAudioDiagnostics(
    long CallbackCount,
    long InputFrames,
    long BufferedFrames,
    long ConsumedFrames,
    long UnderrunCount,
    long OverflowCount,
    long FlushCount,
    long DrainCount,
    long CompletedDrainCount,
    long FlushedBytes,
    long DiscardedLimiterFrames,
    long DiscardedRawFrames,
    long PauseCallbackCount,
    long ResumeCallbackCount,
    int PendingLimiterFrames,
    long NonFiniteInputSamples,
    long NonFiniteOutputSamples,
    double Peak,
    long OverRangeSamples,
    bool IsPaused,
    bool IsRebuffering,
    int BufferedBytes,
    bool SessionNormalized,
    float SessionVolumeBeforeNormalization,
    bool SessionMutedBeforeNormalization,
    bool OutputStarted,
    bool Failed,
    bool RenderThreadAlive,
    long LevelChangeCount,
    int LastLevelChangeBufferedBytes,
    double LastLevelChangeBufferedMilliseconds);

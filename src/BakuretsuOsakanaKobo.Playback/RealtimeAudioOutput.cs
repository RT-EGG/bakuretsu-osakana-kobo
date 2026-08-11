using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BakuretsuOsakanaKobo.Playback;

internal sealed class RealtimeAudioOutput : IDisposable
{
    private static readonly TimeSpan BufferDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PrebufferDuration = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);

    private readonly object _sync = new();
    private readonly Action<Exception> _failureHandler;
    private readonly RealtimeVolumeProcessor _processor = new();
    private readonly BufferedWaveProvider _buffer;
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
    private long _failureReported;
    private long _outputStarted;

    public RealtimeAudioOutput(Action<Exception> failureHandler)
    {
        _failureHandler = failureHandler;
        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            RealtimeVolumeProcessor.SampleRate,
            RealtimeVolumeProcessor.Channels);
        _buffer = new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = BufferDuration,
            DiscardOnBufferOverflow = false,
            ReadFully = false,
        };
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
                return _processor.VolumePercent;
            }
        }
    }

    public bool IsMuted
    {
        get
        {
            lock (_sync)
            {
                return _processor.IsMuted;
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
                    _processor.PendingFrames,
                    _processor.NonFiniteInputSamples,
                    _processor.NonFiniteOutputSamples,
                    _processor.Peak,
                    _processor.OverRangeSamples,
                    Interlocked.Read(ref _outputStarted) != 0,
                    Interlocked.Read(ref _failureReported) != 0,
                    _renderThread.IsAlive);
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
            _processor.SetVolumePercent(volumePercent);
        }
    }

    public void SetMuted(bool isMuted)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _processor.SetMuted(isMuted);
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

                var processed = _processor.Process(pcm16);
                AddToBuffer(processed);
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
                Interlocked.Increment(ref _flushCount);
                Interlocked.Add(ref _flushedBytes, _buffer.BufferedBytes);
                Interlocked.Add(ref _discardedLimiterFrames, _processor.PendingFrames);
                _buffer.ClearBuffer();
                _processor.Reset();
                _isDraining = false;
                _isRebuffering = true;
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
                AddToBuffer(_processor.Flush());
                _isDraining = true;
                _isRebuffering = false;
            }

            _stateChanged.Set();
            var deadline = DateTime.UtcNow + DrainTimeout;
            while (GetBufferedBytes() > 0 && DateTime.UtcNow < deadline && !_shutdown.IsCancellationRequested)
            {
                Thread.Sleep(2);
            }

            if (GetBufferedBytes() > 0 && !_shutdown.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"The PCM drain callback timed out with {GetBufferedBytes()} buffered byte(s).");
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
                        (_buffer.BufferedDuration >= PrebufferDuration || (_isDraining && _buffer.BufferedBytes > 0)))
                    {
                        _isRebuffering = false;
                    }

                    shouldStart = !_isPaused && !_isRebuffering && _buffer.BufferedBytes > 0;
                    shouldPause = !shouldStart;
                }

                if (output is null && shouldStart)
                {
                    enumerator = new MMDeviceEnumerator();
                    endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    output = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 50);
                    output.PlaybackStopped += Output_OnPlaybackStopped;
                    output.Init(new LockedPaddedWaveProvider(this));
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
                    if (_isDraining && _buffer.BufferedBytes == 0)
                    {
                        _isDraining = false;
                        _isRebuffering = true;
                    }
                    else if (!_isPaused && !_isRebuffering && _buffer.BufferedBytes == 0)
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

    private void AddToBuffer(float[] samples)
    {
        if (samples.Length == 0)
        {
            return;
        }

        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        try
        {
            _buffer.AddSamples(bytes, 0, bytes.Length);
            Interlocked.Add(ref _bufferedFrames, samples.Length / RealtimeVolumeProcessor.Channels);
        }
        catch (InvalidOperationException exception)
        {
            Interlocked.Increment(ref _overflowCount);
            throw new InvalidOperationException("The bounded PCM buffer overflowed.", exception);
        }
    }

    private int ReadForRender(byte[] destination, int offset, int count)
    {
        lock (_sync)
        {
            var read = _buffer.Read(destination, offset, count);
            Interlocked.Add(
                ref _consumedFrames,
                read / (_buffer.WaveFormat.BitsPerSample / 8) / _buffer.WaveFormat.Channels);
            if (read < count)
            {
                Array.Clear(destination, offset + read, count - read);
                if (!_isPaused && !_isRebuffering && !_isDraining)
                {
                    Interlocked.Increment(ref _underrunCount);
                }
            }

            return count;
        }
    }

    private int GetBufferedBytes()
    {
        lock (_sync)
        {
            return _buffer.BufferedBytes;
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
        public WaveFormat WaveFormat => owner._buffer.WaveFormat;

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
    int PendingLimiterFrames,
    long NonFiniteInputSamples,
    long NonFiniteOutputSamples,
    double Peak,
    long OverRangeSamples,
    bool OutputStarted,
    bool Failed,
    bool RenderThreadAlive);

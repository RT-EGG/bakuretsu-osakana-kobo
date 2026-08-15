using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace BakuretsuOsakanaKobo.Playback;

public sealed class LibVlcPlaybackBackend : IPlaybackBackend
{
    private readonly LibVLC _libVlc;
    private readonly Action<Exception>? _callbackExceptionHandler;
    private readonly RealtimeAudioOutput _audioOutput;
    private Media? _currentMedia;
    private int _volumePercent = PlaybackVolume.DefaultPercent;
    private bool _isMuted;
    private float _rate = PlaybackRate.Default;
    private double _videoDisplayAspectRatio = PlaybackVideoGeometry.DefaultDisplayAspectRatio;
    private long _knownLengthMilliseconds;
    private long _lastPlaybackTimeMilliseconds;
    private int _preserveTerminalPosition;
    private int _runtimeErrorReported;
    private bool _disposed;

    public LibVlcPlaybackBackend(Action<Exception>? callbackExceptionHandler = null)
    {
        _callbackExceptionHandler = callbackExceptionHandler;
        Core.Initialize();
        _libVlc = new LibVLC(enableDebugLogs: false);
        MediaPlayer? mediaPlayer = null;
        RealtimeAudioOutput? audioOutput = null;

        try
        {
            mediaPlayer = new MediaPlayer(_libVlc);
            MediaPlayer = mediaPlayer;
            audioOutput = new RealtimeAudioOutput(OnAudioOutputFailure);
            audioOutput.AttachTo(MediaPlayer);
            _audioOutput = audioOutput;
            MediaPlayer.Volume = PlaybackVolume.DefaultPercent;
            MediaPlayer.Mute = false;
            SubscribePlayerEvents();
        }
        catch
        {
            mediaPlayer?.Dispose();
            audioOutput?.Dispose();
            _libVlc.Dispose();
            throw;
        }
    }

    public event EventHandler<PlaybackErrorEventArgs>? ErrorOccurred;

    public event EventHandler? StateChanged;

    public event EventHandler? PlaybackEnded;

    public MediaPlayer MediaPlayer { get; }

    public bool IsPlaying
    {
        get
        {
            ThrowIfDisposed();
            return MediaPlayer.IsPlaying;
        }
    }

    public bool IsSeekable
    {
        get
        {
            ThrowIfDisposed();
            return MediaPlayer.IsSeekable;
        }
    }

    public long LengthMilliseconds
    {
        get
        {
            ThrowIfDisposed();
            return Math.Max(
                Math.Max(0, MediaPlayer.Length),
                Interlocked.Read(ref _knownLengthMilliseconds));
        }
    }

    public long TimeMilliseconds
    {
        get
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref _preserveTerminalPosition) != 0)
            {
                return Math.Max(0, Interlocked.Read(ref _lastPlaybackTimeMilliseconds));
            }

            var currentTime = Math.Max(0, MediaPlayer.Time);
            Interlocked.Exchange(ref _lastPlaybackTimeMilliseconds, currentTime);
            return currentTime;
        }
    }

    public int VolumePercent
    {
        get
        {
            ThrowIfDisposed();
            return _volumePercent;
        }
    }

    public bool IsMuted
    {
        get
        {
            ThrowIfDisposed();
            return _isMuted;
        }
    }

    public float Rate
    {
        get
        {
            ThrowIfDisposed();
            return _rate;
        }
    }

    public double VideoDisplayAspectRatio
    {
        get
        {
            ThrowIfDisposed();
            return _videoDisplayAspectRatio;
        }
    }

    public string? CurrentPath { get; private set; }

    internal RealtimeAudioDiagnostics AudioDiagnostics => _audioOutput.Diagnostics;

    public async Task<bool> OpenAndPlayAsync(
        string path,
        PlaybackInitialState? initialState = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            RaiseError(new PlaybackErrorEventArgs(
                "playback-path-invalid",
                "指定された動画の場所を確認できませんでした。",
                "別の動画ファイルを選択してください。",
                exception.Message,
                exception,
                path));
            return false;
        }

        if (!SupportedMediaPolicy.SupportsExtension(fullPath))
        {
            RaisePolicyError(SupportedMediaPolicy.Validate(fullPath, null, null), fullPath);
            return false;
        }

        try
        {
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            RaiseError(new PlaybackErrorEventArgs(
                "playback-file-missing",
                "動画ファイルが見つかりません。",
                "ファイルの場所を確認して、もう一度開いてください。",
                exception.Message,
                exception,
                targetPath: fullPath));
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            RaiseError(new PlaybackErrorEventArgs(
                "playback-file-access-denied",
                "動画ファイルを読み取る権限がありません。",
                "ファイルのアクセス権限を確認してください。",
                exception.Message,
                exception,
                fullPath));
            return false;
        }
        catch (IOException exception)
        {
            RaiseError(new PlaybackErrorEventArgs(
                "playback-file-unreadable",
                "動画ファイルを読み取れませんでした。",
                "ファイルが使用中でないか、状態を確認してください。",
                exception.Message,
                exception,
                fullPath));
            return false;
        }

        Media? nextMedia = null;
        var previousAudioState = new PlaybackAudioState(_volumePercent, _isMuted);
        var nextAudioState = initialState?.AudioState ?? previousAudioState;
        var nextAudioStateApplied = false;
        var nextAudioStateCommitted = false;
        var previousLengthMilliseconds = Interlocked.Read(ref _knownLengthMilliseconds);
        var previousTimeMilliseconds = Interlocked.Read(ref _lastPlaybackTimeMilliseconds);
        var previousPreserveTerminalPosition = Volatile.Read(ref _preserveTerminalPosition);
        var previousRuntimeErrorReported = Volatile.Read(ref _runtimeErrorReported);
        var nextPlaybackStateApplied = false;
        try
        {
            nextMedia = new Media(_libVlc, new Uri(fullPath));
            var parseStatus = await nextMedia.Parse(
                MediaParseOptions.ParseLocal,
                5_000,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (parseStatus != MediaParsedStatus.Done)
            {
                RaiseError(new PlaybackErrorEventArgs(
                    "playback-parse-failed",
                    "動画ファイルを解析できませんでした。",
                    "ファイルが破損していないか確認してください。",
                    $"LibVLC media parse status was {parseStatus}.",
                    targetPath: fullPath));
                return false;
            }

            var videoTrack = nextMedia.Tracks
                .FirstOrDefault(track => track.TrackType == TrackType.Video);
            var videoCodec = videoTrack.Codec;
            var audioCodec = nextMedia.Tracks
                .FirstOrDefault(track => track.TrackType == TrackType.Audio)
                .Codec;
            var mediaPolicy = SupportedMediaPolicy.Validate(
                fullPath,
                FourCc(videoCodec),
                FourCc(audioCodec));
            if (!mediaPolicy.IsAccepted)
            {
                RaisePolicyError(mediaPolicy, fullPath);
                return false;
            }

            // A staging player is only needed when a failed candidate must not disturb an
            // existing playback session. Avoid decoding the first video twice on startup.
            if (CurrentPath is not null &&
                !await CanStartInStagingPlayerAsync(fullPath, cancellationToken).ConfigureAwait(false))
            {
                RaiseError(new PlaybackErrorEventArgs(
                    "playback-staging-failed",
                    "動画の再生準備を完了できませんでした。",
                    "ファイルが破損していないか確認してください。",
                    "The muted staging MediaPlayer did not enter the playing state.",
                    targetPath: fullPath));
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _volumePercent = nextAudioState.VolumePercent;
            _isMuted = nextAudioState.IsMuted;
            nextAudioStateApplied = true;
            ApplyVolumeState();
            _audioOutput.PrepareForPlayback();
            var nextLengthMilliseconds = Math.Max(0, nextMedia.Duration);
            var initialPosition = PlaybackPosition.ResolveInitialPosition(
                initialState?.StartPositionMilliseconds,
                nextLengthMilliseconds);
            Interlocked.Exchange(ref _knownLengthMilliseconds, nextLengthMilliseconds);
            Interlocked.Exchange(
                ref _lastPlaybackTimeMilliseconds,
                initialPosition is null
                    ? 0
                    : (long)Math.Round(initialPosition.Value * nextLengthMilliseconds));
            Volatile.Write(ref _preserveTerminalPosition, 0);
            Volatile.Write(ref _runtimeErrorReported, 0);
            nextPlaybackStateApplied = true;
            if (!MediaPlayer.Play(nextMedia))
            {
                RaiseError(new PlaybackErrorEventArgs(
                    "playback-start-rejected",
                    "動画の再生を開始できませんでした。",
                    "対応形式とファイルの状態を確認してください。",
                    "LibVLC rejected the play request.",
                    targetPath: fullPath));
                return false;
            }

            var videoData = videoTrack.Data.Video;
            var nextVideoDisplayAspectRatio = PlaybackVideoGeometry.DisplayAspectRatio(
                videoData.Width,
                videoData.Height,
                videoData.SarNum,
                videoData.SarDen,
                SwapsVideoAxes(videoData.Orientation));

            if (MediaPlayer.SetRate(PlaybackRate.Default) != 0)
            {
                RaiseError(new PlaybackErrorEventArgs(
                    "playback-default-rate-rejected",
                    "新しい動画を標準速度へ戻せませんでした。",
                    "右クリックメニューから1.0倍を選び直してください。",
                    "LibVLC rejected the default 1.0 playback rate after opening media.",
                    targetPath: fullPath));
            }
            else
            {
                _rate = PlaybackRate.Default;
            }

            if (initialPosition is not null)
            {
                MediaPlayer.Position = (float)initialPosition.Value;
            }

            var previousMedia = _currentMedia;
            _currentMedia = nextMedia;
            nextMedia = null;
            CurrentPath = fullPath;
            _videoDisplayAspectRatio = nextVideoDisplayAspectRatio;
            nextAudioStateCommitted = true;
            DisposeResource(previousMedia);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            RaiseError(new PlaybackErrorEventArgs(
                "playback-open-failed",
                "動画を開けませんでした。",
                "対応形式とファイルの状態を確認してください。",
                exception.Message,
                exception,
                fullPath));
            return false;
        }
        finally
        {
            if (nextAudioStateApplied && !nextAudioStateCommitted)
            {
                _volumePercent = previousAudioState.VolumePercent;
                _isMuted = previousAudioState.IsMuted;
                ApplyVolumeState();
            }

            if (nextPlaybackStateApplied && !nextAudioStateCommitted)
            {
                Interlocked.Exchange(ref _knownLengthMilliseconds, previousLengthMilliseconds);
                Interlocked.Exchange(ref _lastPlaybackTimeMilliseconds, previousTimeMilliseconds);
                Volatile.Write(ref _preserveTerminalPosition, previousPreserveTerminalPosition);
                Volatile.Write(ref _runtimeErrorReported, previousRuntimeErrorReported);
            }

            if (nextMedia is not null)
            {
                try
                {
                    nextMedia.ParseStop();
                }
                catch (Exception exception)
                {
                    ReportCallbackException(exception);
                }

                // Parse() completes from a native notification. Keep rejected media alive until
                // the notification has fully unwound before releasing its native handle.
                await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
                DisposeResource(nextMedia);
            }
        }
    }

    public void Play()
    {
        ThrowIfDisposed();
        _audioOutput.PrepareForPlayback();
        MediaPlayer.Play();
    }

    public void Pause()
    {
        ThrowIfDisposed();
        MediaPlayer.Pause();
    }

    public void Stop()
    {
        ThrowIfDisposed();
        MediaPlayer.Stop();
    }

    public void Seek(double normalizedPosition)
    {
        ThrowIfDisposed();
        MediaPlayer.Position = (float)PlaybackPosition.Normalize(normalizedPosition);
    }

    public void SetVolumePercent(int volumePercent)
    {
        ThrowIfDisposed();
        _volumePercent = PlaybackVolume.Clamp(volumePercent);
        _audioOutput.SetVolumePercent(_volumePercent);
    }

    public void SetMuted(bool isMuted)
    {
        ThrowIfDisposed();
        _isMuted = isMuted;
        _audioOutput.SetMuted(isMuted);
    }

    public bool TrySetRate(float rate)
    {
        ThrowIfDisposed();
        if (CurrentPath is null || !PlaybackRate.IsSupported(rate))
        {
            return false;
        }

        if (PlaybackRate.AreEqual(_rate, rate))
        {
            return true;
        }

        _audioOutput.PrepareForRateChange();
        if (MediaPlayer.SetRate(rate) != 0)
        {
            return false;
        }

        _rate = rate;
        RaiseSafely(StateChanged, EventArgs.Empty);
        return true;
    }

    private void ApplyVolumeState()
    {
        _audioOutput.SetVolumePercent(_volumePercent);
        _audioOutput.SetMuted(_isMuted);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnsubscribePlayerEvents();

        try
        {
            MediaPlayer.Stop();
        }
        catch (Exception exception)
        {
            ReportCallbackException(exception);
        }

        DisposeResource(_currentMedia);
        _currentMedia = null;
        DisposeResource(MediaPlayer);
        _audioOutput.Dispose();
        DisposeResource(_libVlc);
    }

    private void SubscribePlayerEvents()
    {
        MediaPlayer.Playing += OnStateChanged;
        MediaPlayer.Paused += OnStateChanged;
        MediaPlayer.Stopped += OnStateChanged;
        MediaPlayer.TimeChanged += OnTimeChanged;
        MediaPlayer.EndReached += OnPlaybackEnded;
        MediaPlayer.EncounteredError += OnEncounteredError;
    }

    private void UnsubscribePlayerEvents()
    {
        MediaPlayer.Playing -= OnStateChanged;
        MediaPlayer.Paused -= OnStateChanged;
        MediaPlayer.Stopped -= OnStateChanged;
        MediaPlayer.TimeChanged -= OnTimeChanged;
        MediaPlayer.EndReached -= OnPlaybackEnded;
        MediaPlayer.EncounteredError -= OnEncounteredError;
    }

    private void OnStateChanged(object? sender, EventArgs eventArgs) => RaiseSafely(StateChanged, EventArgs.Empty);

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs eventArgs)
    {
        if (!_disposed && Volatile.Read(ref _preserveTerminalPosition) == 0)
        {
            Interlocked.Exchange(ref _lastPlaybackTimeMilliseconds, Math.Max(0, eventArgs.Time));
        }
    }

    private void OnPlaybackEnded(object? sender, EventArgs eventArgs)
    {
        if (_disposed)
        {
            return;
        }

        var lengthMilliseconds = Math.Max(
            ReadPlayerLengthSafely(),
            Interlocked.Read(ref _knownLengthMilliseconds));
        var playbackTimeMilliseconds = Math.Max(
            ReadPlayerTimeSafely(),
            Interlocked.Read(ref _lastPlaybackTimeMilliseconds));
        if (PlaybackCompletion.IsPrematureEnd(playbackTimeMilliseconds, lengthMilliseconds))
        {
            ReportRuntimeError(new PlaybackErrorEventArgs(
                "playback-ended-early",
                "動画ファイルの破損により、再生を最後まで続けられませんでした。",
                "別の動画を開くか、元のファイルを確認してください。",
                $"Playback ended at {playbackTimeMilliseconds} ms before the declared " +
                $"length of {lengthMilliseconds} ms.",
                targetPath: CurrentPath),
                playbackTimeMilliseconds);
            return;
        }

        RaiseSafely(StateChanged, EventArgs.Empty);
        if (Volatile.Read(ref _runtimeErrorReported) == 0)
        {
            RaiseSafely(PlaybackEnded, EventArgs.Empty);
        }
    }

    private void OnEncounteredError(object? sender, EventArgs eventArgs) =>
        ReportRuntimeError(new PlaybackErrorEventArgs(
            "playback-native-error",
            "動画の再生中に問題が発生しました。",
            "別の動画を開くか、ファイルの状態を確認してください。",
            "LibVLC raised the MediaPlayer.EncounteredError event.",
            targetPath: CurrentPath));

    private void OnAudioOutputFailure(Exception exception) =>
        RaiseError(new PlaybackErrorEventArgs(
            "playback-audio-output-error",
            "音声の再生中に問題が発生しました。",
            "Windowsの音声出力を確認して、アプリを再起動してください。",
            exception.Message,
            exception,
            CurrentPath));

    private void ReportRuntimeError(
        PlaybackErrorEventArgs eventArgs,
        long? playbackTimeMilliseconds = null)
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _runtimeErrorReported, 1) != 0)
        {
            return;
        }

        var terminalTime = Math.Max(
            0,
            playbackTimeMilliseconds ?? Math.Max(
                ReadPlayerTimeSafely(),
                Interlocked.Read(ref _lastPlaybackTimeMilliseconds)));
        Interlocked.Exchange(ref _lastPlaybackTimeMilliseconds, terminalTime);
        Volatile.Write(ref _preserveTerminalPosition, 1);
        RaiseSafely(StateChanged, EventArgs.Empty);
        RaiseError(eventArgs);
    }

    private long ReadPlayerTimeSafely()
    {
        try
        {
            return Math.Max(0, MediaPlayer.Time);
        }
        catch (Exception exception)
        {
            ReportCallbackException(exception);
            return Math.Max(0, Interlocked.Read(ref _lastPlaybackTimeMilliseconds));
        }
    }

    private long ReadPlayerLengthSafely()
    {
        try
        {
            return Math.Max(0, MediaPlayer.Length);
        }
        catch (Exception exception)
        {
            ReportCallbackException(exception);
            return Math.Max(0, Interlocked.Read(ref _knownLengthMilliseconds));
        }
    }

    private void RaiseError(PlaybackErrorEventArgs eventArgs) => RaiseSafely(ErrorOccurred, eventArgs);

    private async Task<bool> CanStartInStagingPlayerAsync(string path, CancellationToken cancellationToken)
    {
        Media? stagingMedia = null;
        MediaPlayer? stagingPlayer = null;
        DiscardingVideoSink? videoSink = null;

        try
        {
            stagingMedia = new Media(_libVlc, new Uri(path));
            stagingMedia.AddOption(":aout=dummy");
            stagingPlayer = new MediaPlayer(_libVlc)
            {
                Mute = true,
            };
            videoSink = new DiscardingVideoSink(ReportCallbackException);
            videoSink.Attach(stagingPlayer);

            if (!stagingPlayer.Play(stagingMedia))
            {
                return false;
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(1_500);
            while (!stagingPlayer.IsPlaying && DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stagingPlayer.State is VLCState.Error or VLCState.Ended or VLCState.Stopped)
                {
                    return false;
                }

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }

            return stagingPlayer.IsPlaying;
        }
        finally
        {
            if (stagingPlayer is not null)
            {
                try
                {
                    stagingPlayer.Stop();
                }
                catch (Exception exception)
                {
                    ReportCallbackException(exception);
                }

                // Stop() can return while LibVLC is still unwinding decoder work on another thread.
                // Keep the staging objects alive briefly before releasing their native handles.
                await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
            }

            DisposeResource(stagingPlayer);
            videoSink?.Dispose();
            DisposeResource(stagingMedia);
        }
    }

    private sealed class DiscardingVideoSink : IDisposable
    {
        private readonly IntPtr _buffer = Marshal.AllocHGlobal(sizeof(int));
        private readonly Action<Exception> _exceptionHandler;
        private readonly MediaPlayer.LibVLCVideoLockCb _lockCallback;
        private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCallback;
        private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCallback;
        private bool _disposed;

        public DiscardingVideoSink(Action<Exception> exceptionHandler)
        {
            _exceptionHandler = exceptionHandler;
            _lockCallback = Lock;
            _unlockCallback = static (_, _, _) => { };
            _displayCallback = static (_, _) => { };
        }

        public void Attach(MediaPlayer player)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            player.SetVideoCallbacks(_lockCallback, _unlockCallback, _displayCallback);
            player.SetVideoFormat("RV32", 1, 1, sizeof(int));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Marshal.FreeHGlobal(_buffer);
        }

        private IntPtr Lock(IntPtr opaque, IntPtr planes)
        {
            try
            {
                if (!_disposed)
                {
                    Marshal.WriteIntPtr(planes, _buffer);
                }
            }
            catch (Exception exception)
            {
                try
                {
                    _exceptionHandler(exception);
                }
                catch
                {
                    // Managed exceptions must never cross the native LibVLC callback boundary.
                }
            }

            return IntPtr.Zero;
        }
    }

    private void RaisePolicyError(MediaPolicyResult result, string targetPath) =>
        RaiseError(new PlaybackErrorEventArgs(
            result.EventCode!,
            result.UserMessage!,
            result.SuggestedAction!,
            result.TechnicalMessage!,
            targetPath: targetPath));

    private static string? FourCc(uint value)
    {
        if (value == 0)
        {
            return null;
        }

        Span<char> characters = stackalloc char[4];
        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = (char)((value >> (index * 8)) & 0xff);
        }

        return new string(characters).TrimEnd('\0');
    }

    private static bool SwapsVideoAxes(VideoOrientation orientation) => orientation is
        VideoOrientation.LeftTop or
        VideoOrientation.LeftBottom or
        VideoOrientation.RightTop or
        VideoOrientation.RightBottom;

    private void RaiseSafely(EventHandler? handlers, EventArgs eventArgs)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                ReportCallbackException(exception);
            }
        }
    }

    private void RaiseSafely<TEventArgs>(EventHandler<TEventArgs>? handlers, TEventArgs eventArgs)
        where TEventArgs : EventArgs
    {
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, eventArgs);
            }
            catch (Exception exception)
            {
                ReportCallbackException(exception);
            }
        }
    }

    private void ReportCallbackException(Exception exception)
    {
        try
        {
            _callbackExceptionHandler?.Invoke(exception);
        }
        catch
        {
            // A managed exception must never escape a native LibVLC callback.
        }
    }

    private void DisposeResource(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception exception)
        {
            ReportCallbackException(exception);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
